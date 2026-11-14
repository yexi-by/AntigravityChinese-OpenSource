using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AntigravityChinese.Core;

// 服务下发的已知界面文案；仅用于经过语法树确认的套餐、配额和系统命令显示消费者。
internal static class ServiceDisplay
{
    public const string QuotaGroup = "service/quota-group";
    public const string QuotaLabel = "service/quota-label";
    public const string QuotaTitle = "service/quota-title";
    public const string QuotaDetail = "service/quota-detail";
    public const string QuotaDescription = "service/quota-description";
    public const string PlanDescription = "service/plan-description";
    public const string CommandDescription = "service/command-description";
    private const string Name = @"[^\r\n]{1,160}";
    private const string Duration = @"[0-9]+ (?:days?|hours?|minutes?|seconds?)(?:,? (?:and )?[0-9]+ (?:days?|hours?|minutes?|seconds?)){0,3}";
    private const string CompactUnit = @"[0-9]+[smhd]";
    private const string CompactDuration = @"[0-9]+[dhms](?: [0-9]+[dhms])?";
    private enum DurationMode { None, LongWords, Compact }
    private sealed record Rule(string Source, string? Slot = null, DurationMode Duration = DurationMode.None);
    private static readonly Rule[] Units = [.. new[] { "day", "days", "hour", "hours", "minute", "minutes", "second", "seconds" }
        .Select(unit => new Rule("⟪AGC0⟫ " + unit, "[0-9]+"))];
    private static readonly Rule[] CompactUnits =
    [
        new("⟪AGC0⟫d ⟪AGC1⟫h", "[0-9]+"),
        new("⟪AGC0⟫h ⟪AGC1⟫m", "[0-9]+"),
        new("⟪AGC0⟫d", "[0-9]+"),
        new("⟪AGC0⟫h", "[0-9]+"),
        new("⟪AGC0⟫m", "[0-9]+"),
        new("⟪AGC0⟫s", "[0-9]+")
    ];
    private static readonly Rule[] CompactQuotaRules =
    [
        new("Daily limit"), new("Weekly limit"),
        new("⟪AGC0⟫ limit", CompactUnit, DurationMode.Compact),
        new("Resets in <1m"),
        new("Resets in ⟪AGC0⟫", CompactDuration, DurationMode.Compact),
        new("Daily limit, resets in <1m"),
        new("Weekly limit, resets in <1m"),
        new("⟪AGC0⟫ limit, resets in <1m", CompactUnit, DurationMode.Compact),
        new("Daily limit, resets in ⟪AGC0⟫", CompactDuration, DurationMode.Compact),
        new("Weekly limit, resets in ⟪AGC0⟫", CompactDuration, DurationMode.Compact),
        new("⟪AGC0⟫ limit, resets in ⟪AGC1⟫", CompactDuration, DurationMode.Compact),
        .. CompactUnits
    ];
    private static readonly Dictionary<string, Rule[]> Rules = new(StringComparer.Ordinal)
    {
        [QuotaGroup] = [new("Gemini Models"), new("Claude and GPT models")],
        [QuotaLabel] = [new("Weekly Limit Remaining"), new("Five Hour Limit Remaining"), new("⟪AGC0⟫ Weekly Limit", Name)],
        [QuotaTitle] =
        [
            new("Gemini Models"), new("Claude and GPT models"),
            .. CompactQuotaRules
        ],
        [QuotaDetail] = CompactQuotaRules,
        [QuotaDescription] = [new("Models within this group: ⟪AGC0⟫", @"[^\r\n]{1,2048}"),
            new("You have used some of your weekly limit, it will fully refresh in ⟪AGC0⟫.", Duration, DurationMode.LongWords), .. Units],
        [PlanDescription] = [new("You can upgrade to a ⟪AGC0⟫ plan to receive higher rate limits.", Name),
            new("You can upgrade to a higher ⟪AGC0⟫ plan to receive the highest rate limits.", Name)],
        [CommandDescription] = [.. new[] {
            "Run until the specified goal is completely finished.",
            "Compact the conversation now (summarize history to free up the context window).",
            "Run an instruction on a recurring schedule or as a one-time timer.",
            "Invoke a browser agent for web tasks.",
            "Plan carefully before executing a task.",
            "Interview me to align on a plan.",
            "Invoke a team of agents to autonomously tackle large projects.",
            "Reflect on recent successes or corrections to capture reusable skills or rules.",
            "Invoke the Boost multi-agent orchestrator for complex tasks.",
            "[Teamfood] Invoke a browser with computer-use tools only with experimental model."
        }.Select(source => new Rule(source))]
    };
    public static IEnumerable<string> Sources(string role) => Rules[role].Select(r => r.Source);

    public static string Render(string expression, Dictionary<string, string> translations)
    {
        var definitions = Rules.Values.SelectMany(r => r).DistinctBy(r => r.Source).ToDictionary(r => r.Source);
        var rules = translations.Select(pair =>
        {
            var definition = definitions[pair.Key];
            if (!JavaScriptUi.Slots().Matches(pair.Key).Select(m => m.Value).SequenceEqual(JavaScriptUi.Slots().Matches(pair.Value).Select(m => m.Value)))
                throw new InvalidDataException("服务界面文案的动态占位符发生变化。");
            var pattern = new StringBuilder("^"); var position = 0;
            foreach (Match slot in JavaScriptUi.Slots().Matches(pair.Key))
            {
                pattern.Append(Regex.Escape(pair.Key[position..slot.Index])).Append('(').Append(definition.Slot).Append(')');
                position = slot.Index + slot.Length;
            }
            pattern.Append(Regex.Escape(pair.Key[position..])).Append('$');
            return new { pattern = pattern.ToString(), target = pair.Value, duration = (int)definition.Duration };
        });
        // 捕获值经回调写回，名称中的 $ 等字符不会被解释为替换语法；表达式只求值一次。
        return $$"""
        ((value)=>{
          const rules={{JsonSerializer.Serialize(rules)}};
          function convert(text) {
            if(typeof text!=='string')return text;
            for(const rule of rules) {
              const match=new RegExp(rule.pattern).exec(text);
              if(!match)continue;
              const slots=match.slice(1);
              if(rule.duration===1) {
                const parts=slots[0].match(/[0-9]+ (?:days?|hours?|minutes?|seconds?)/g);
                const formatted=parts.map(part=>convert(part));
                if(formatted.some((part,index)=>part===parts[index]))return text;
                slots[0]=formatted.join(' ');
              } else if(rule.duration===2) {
                for(let i=0;i<slots.length;i++) {
                  const converted=convert(slots[i]);
                  if(converted===slots[i])return text;
                  slots[i]=converted;
                }
              }
              return rule.target.replace(/⟪AGC([0-9]+)⟫/g,(_,index)=>slots[Number(index)]);
            }
            return text;
          }
          return convert(value);
        })({{expression}})
        """;
    }
}
