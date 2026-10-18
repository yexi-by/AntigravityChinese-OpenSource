using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AntigravityChinese.Core;

// 服务下发的已知界面文案；仅用于经过语法树确认的套餐、配额显示消费者。
internal static class ServiceDisplay
{
    public const string QuotaGroup = "service/quota-group";
    public const string QuotaLabel = "service/quota-label";
    public const string QuotaDescription = "service/quota-description";
    public const string PlanDescription = "service/plan-description";
    private const string Name = @"[^\r\n]{1,160}";
    private const string Duration = @"[0-9]+ (?:days?|hours?|minutes?|seconds?)(?:,? (?:and )?[0-9]+ (?:days?|hours?|minutes?|seconds?)){0,3}";
    private sealed record Rule(string Source, string? Slot = null, bool IsDuration = false);
    private static readonly Rule[] Units = [.. new[] { "day", "days", "hour", "hours", "minute", "minutes", "second", "seconds" }
        .Select(unit => new Rule("⟪AGC0⟫ " + unit, "[0-9]+"))];
    private static readonly Dictionary<string, Rule[]> Rules = new(StringComparer.Ordinal)
    {
        [QuotaGroup] = [new("Gemini Models"), new("Claude and GPT models")],
        [QuotaLabel] = [new("Weekly Limit Remaining"), new("Five Hour Limit Remaining"), new("⟪AGC0⟫ Weekly Limit", Name)],
        [QuotaDescription] = [new("Models within this group: ⟪AGC0⟫", @"[^\r\n]{1,2048}"),
            new("You have used some of your weekly limit, it will fully refresh in ⟪AGC0⟫.", Duration, true), .. Units],
        [PlanDescription] = [new("You can upgrade to a ⟪AGC0⟫ plan to receive higher rate limits.", Name)]
    };
    public static IEnumerable<string> Sources(string role) => Rules[role].Select(r => r.Source);

    public static string Render(string expression, Dictionary<string, string> translations)
    {
        var definitions = Rules.Values.SelectMany(r => r).DistinctBy(r => r.Source).ToDictionary(r => r.Source);
        var rules = translations.Select(pair =>
        {
            var definition = definitions[pair.Key];
            ProtectedTerms.Validate(pair.Key, pair.Value);
            if (!JavaScriptUi.Slots().Matches(pair.Key).Select(m => m.Value).SequenceEqual(JavaScriptUi.Slots().Matches(pair.Value).Select(m => m.Value)))
                throw new InvalidDataException("服务界面文案的动态占位符发生变化。");
            var pattern = new StringBuilder("^"); var position = 0;
            foreach (Match slot in JavaScriptUi.Slots().Matches(pair.Key))
            {
                pattern.Append(Regex.Escape(pair.Key[position..slot.Index])).Append('(').Append(definition.Slot).Append(')');
                position = slot.Index + slot.Length;
            }
            pattern.Append(Regex.Escape(pair.Key[position..])).Append('$');
            return new { pattern = pattern.ToString(), target = pair.Value, duration = definition.IsDuration };
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
              if(rule.duration) {
                const parts=slots[0].match(/[0-9]+ (?:days?|hours?|minutes?|seconds?)/g);
                const formatted=parts.map(part=>convert(part));
                if(formatted.some((part,index)=>part===parts[index]))return text;
                slots[0]=formatted.join(' ');
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
