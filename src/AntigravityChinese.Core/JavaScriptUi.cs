using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Acornima;
using Acornima.Ast;

namespace AntigravityChinese.Core;

public static partial class JavaScriptUi
{
    private static readonly HashSet<string> TextProperties = new(StringComparer.Ordinal)
    {
        "label", "title", "description", "placeholder", "buttonTitle", "helperText", "message", "detail",
        "checkboxLabel", "caption", "alt", "tooltip", "notificationTitle", "warningText", "aria-label",
        "buttonText", "submitText", "cancelText", "confirmText", "emptyMessage", "header", "primaryText",
        "secondaryText", "defaultMessage", "emptyText", "loadingText", "errorMessage", "text", "tooltipText",
        "displayText", "subtitle", "subTitle", "primaryCtaText", "secondaryCtaText", "ariaLabel", "disabledWorktreeSubtitle"
    };
    private static readonly JsonSerializerOptions Strings = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static Node Parse(string code)
    {
        try { return new Parser().ParseScript(code); }
        catch (ParseErrorException) { return new Parser().ParseModule(code); }
    }
    public static void Validate(string code) => Parse(code);
    public static bool MayContainUi(string code) => UiCalls().IsMatch(code);
    public static string Quote(string text) => JsonSerializer.Serialize(text, Strings);
    private static string PropertyName(ObjectProperty p) => p.Computed ? "" : p.Key switch
    { Identifier i => i.Name, StringLiteral s => s.Value, _ => "" };

    public static List<UiGroup> Extract(string code, string file, bool desktop = false)
    {
        var tree = Parse(code);
        var groups = new List<UiGroup>();
        var seen = new HashSet<int>();
        var declarations = new Dictionary<(int Function, string Name), Expression>();
        var nodes = Walk(tree).ToArray();
        var protectedRanges = new List<(int Start, int End)>();
        foreach (var call in nodes.Select(p => p.Node).OfType<CallExpression>())
        {
            Expression callee = call.Callee; if (callee is SequenceExpression sequence) callee = sequence.Expressions[^1];
            if (callee is not MemberExpression { Property: Identifier method } || method.Name is not ("createElement" or "jsx" or "jsxs" or "jsxDEV") || call.Arguments.Count < 2) continue;
            var tag = (call.Arguments[0] as StringLiteral)?.Value;
            var properties = (call.Arguments[1] as ObjectExpression)?.Properties.OfType<ObjectProperty>().ToArray() ?? [];
            var codeProperties = properties.Any(p => PropertyName(p) is "language" or "languageId");
            var css = properties.FirstOrDefault(p => PropertyName(p) == "className")?.Value;
            var classes = css is null ? "" : string.Join(' ', Walk(css).Select(p => p.Node).OfType<StringLiteral>().Select(s => s.Value));
            var styles = (properties.FirstOrDefault(p => PropertyName(p) == "style")?.Value as ObjectExpression)?.Properties.OfType<ObjectProperty>().ToArray() ?? [];
            var preformatted = styles.Any(p => PropertyName(p) == "whiteSpace" && p.Value is StringLiteral { Value: "pre" or "pre-wrap" or "pre-line" }) ||
                Regex.IsMatch(classes, @"(?:^|\s)whitespace-pre(?:-wrap|-line)?(?:\s|$)");
            var monospace = classes.Contains("font-mono", StringComparison.Ordinal) || styles.Any(p => PropertyName(p) == "fontFamily" && p.Value is StringLiteral font && font.Value.Contains("mono", StringComparison.OrdinalIgnoreCase));
            if (tag is "style" or "script" or "code" or "pre" or "kbd" or "textarea" || codeProperties || preformatted && monospace) protectedRanges.Add((call.Start, call.End));
        }
        var protectedBlocks = new List<(int Start, int End)>();
        foreach (var range in protectedRanges.OrderBy(r => r.Start).ThenByDescending(r => r.End))
        {
            if (protectedBlocks.Count > 0 && range.Start <= protectedBlocks[^1].End)
                protectedBlocks[^1] = (protectedBlocks[^1].Start, Math.Max(protectedBlocks[^1].End, range.End));
            else protectedBlocks.Add(range);
        }
        var protectedStarts = protectedBlocks.Select(r => r.Start).ToArray();
        bool IsProtected(Node node)
        {
            var index = Array.BinarySearch(protectedStarts, node.Start);
            if (index < 0) index = ~index - 1;
            return index >= 0 && node.End <= protectedBlocks[index].End;
        }
        var scopes = new Dictionary<Node, int>(ReferenceEqualityComparer.Instance);
        foreach (var pair in nodes) scopes[pair.Node] = pair.Function;
        var parents = new Dictionary<int, int>(); var nesting = new Stack<Node>();
        foreach (var function in nodes.Select(p => p.Node).Where(IsFunction).OrderBy(n => n.Start).ThenByDescending(n => n.End))
        {
            while (nesting.TryPeek(out var enclosing) && enclosing.End <= function.Start) nesting.Pop();
            parents[function.Start] = nesting.TryPeek(out var parent) ? parent.Start : -1; nesting.Push(function);
        }
        var bindings = new HashSet<(int Function, string Name)>(); var ambiguous = new HashSet<(int Function, string Name)>();
        var destructured = new Dictionary<(int Function, string Name), (Expression Value, string Property)>();
        var namedFunctions = new Dictionary<(int Function, string Name), Node>();
        void Declare(int scope, string name) { if (!bindings.Add((scope, name))) ambiguous.Add((scope, name)); }
        foreach (var (node, function) in nodes)
        {
            foreach (var parameter in Parameters(node)) foreach (var name in BindingNames(parameter)) Declare(function, name);
            if (node is FunctionDeclaration { Id: Identifier declared })
            { Declare(parents[function], declared.Name); namedFunctions[(parents[function], declared.Name)] = node; }
            if (node is not VariableDeclarator variable) continue;
            foreach (var name in BindingNames(variable.Id)) Declare(function, name);
            if (variable.Init is not Expression initial) continue;
            if (variable.Id is Identifier id)
            {
                declarations[(function, id.Name)] = initial;
                if (IsFunction(initial)) namedFunctions[(function, id.Name)] = initial;
            }
            else if (variable.Id is ObjectPattern pattern)
                foreach (var property in pattern.Properties.OfType<AssignmentProperty>())
                    if (!property.Computed && property.Key is Identifier key && property.Value is Identifier value)
                        destructured[(function, value.Name)] = (initial, key.Name);
        }
        int? Owner(int scope, string name)
        {
            while (true)
            {
                if (bindings.Contains((scope, name))) return scope;
                if (scope == -1) return null;
                scope = parents[scope];
            }
        }
        var callbacks = new Dictionary<(int Function, string Name), (Expression Collection, int Outer)>();
        var assigned = new HashSet<(int Function, string Name)>();
        var writes = new Dictionary<(int Function, string Name), List<(int Position, int End, Expression? Value, bool Linear, bool Closure)>>();
        var linear = new HashSet<int>();
        var linearReads = new HashSet<int>();
        void MarkReads(Node node)
        {
            var pending = new Stack<Node>(); pending.Push(node);
            while (pending.TryPop(out var current))
            {
                if (IsFunction(current)) continue;
                if (current is Identifier) linearReads.Add(current.Start);
                foreach (var child in current.ChildNodes) pending.Push(child);
            }
        }
        void MarkLinear(Expression expression)
        {
            if (expression is SequenceExpression sequence) foreach (var item in sequence.Expressions) MarkLinear(item);
            else if (expression is AssignmentExpression { Left: Identifier }) linear.Add(expression.Start);
        }
        var bodies = new[] { tree }.Cast<Node>().Concat(nodes.Select(p => p.Node).Where(IsFunction).Select(n => n switch
        { FunctionDeclaration f => (Node)f.Body, FunctionExpression f => f.Body, ArrowFunctionExpression f => f.Body, _ => n }));
        foreach (var body in bodies)
        {
            foreach (var statement in body.ChildNodes.OfType<ExpressionStatement>()) MarkLinear(statement.Expression);
            foreach (var declaration in body.ChildNodes.OfType<VariableDeclaration>())
                foreach (var variable in declaration.Declarations) if (variable.Init is not null) MarkReads(variable.Init);
        }
        foreach (var (node, function) in nodes)
        {
            Node? target = node switch { AssignmentExpression a => a.Left, UpdateExpression u => u.Argument, _ => null };
            if (target is null) continue;
            var direct = target is Identifier;
            while (target is MemberExpression member) target = member.Object;
            foreach (var name in target is Identifier id ? new[] { id.Name } : BindingNames(target))
            {
                var scope = Owner(function, name) ?? function; var key = (scope, name);
                assigned.Add(key);
                if (!writes.TryGetValue(key, out var values)) writes[key] = values = [];
                values.Add((node.Start, node.End, node is AssignmentExpression assignment ? assignment.Right : null, direct && scope == function && linear.Contains(node.Start), scope != function));
            }
        }
        var fixedBindings = bindings.Except(assigned).Except(ambiguous).ToHashSet();
        // 配额接口返回的是显示文案。按组件参数和字段契约确认消费者，不转换接口原始数据。
        var serviceParameters = new Dictionary<(int Function, string Name), string>();
        var quotaGroups = new HashSet<(int Function, string Name)>();
        foreach (var producer in nodes.Select(p => p.Node).Where(IsFunction))
        foreach (var parameter in Parameters(producer).OfType<ObjectPattern>())
        {
            var parameters = parameter.Properties.OfType<AssignmentProperty>()
                .Where(p => !p.Computed && p.Key is Identifier && p.Value is Identifier)
                .GroupBy(p => ((Identifier)p.Key).Name).Where(g => g.Count() == 1)
                .ToDictionary(g => g.Key, g => ((Identifier)g.Single().Value).Name);
            if (parameters.Keys.ToHashSet().IsSupersetOf(["label", "remainingFraction", "refreshText", "disabled"]))
            {
                serviceParameters[(producer.Start, parameters["label"])] = ServiceDisplay.QuotaLabel;
                serviceParameters[(producer.Start, parameters["refreshText"])] = ServiceDisplay.QuotaDescription;
            }
            if (parameters.TryGetValue("group", out var group) && parameters.ContainsKey("sharedModels") &&
                Walk(producer).Select(p => p.Node).OfType<MemberExpression>().Any(m =>
                    !m.Computed && m.Object is Identifier id && id.Name == group && m.Property is Identifier { Name: "buckets" }))
                quotaGroups.Add((producer.Start, group));
        }
        string? ServiceRole(Expression expression, int function, HashSet<(int, string)> visited)
        {
            if (expression is ChainExpression chain) return ServiceRole(chain.Expression, function, visited);
            if (expression is LogicalExpression logical)
                return ServiceRole(logical.Right, function, new(visited)) ??
                    (logical.Right is StringLiteral { Value: "" } ? ServiceRole(logical.Left, function, visited) : null);
            if (expression is MemberExpression { Computed: false } member)
            {
                if (member.Property is Identifier { Name: "upgradeSubscriptionText" } &&
                    member.Object is MemberExpression { Computed: false, Property: Identifier { Name: "userTier" } }) return ServiceDisplay.PlanDescription;
                if (member.Object is Identifier group && Owner(function, group.Name) is { } groupOwner &&
                    fixedBindings.Contains((groupOwner, group.Name)) && quotaGroups.Contains((groupOwner, group.Name)))
                    return member.Property is Identifier { Name: "displayName" } ? ServiceDisplay.QuotaGroup :
                        member.Property is Identifier { Name: "description" } ? ServiceDisplay.QuotaDescription : null;
            }
            if (expression is not Identifier variable || Owner(function, variable.Name) is not { } owner || !visited.Add((owner, variable.Name))) return null;
            if (fixedBindings.Contains((owner, variable.Name)) && serviceParameters.TryGetValue((owner, variable.Name), out var role)) return role;
            return fixedBindings.Contains((owner, variable.Name)) && declarations.TryGetValue((owner, variable.Name), out var initial)
                ? ServiceRole(initial, owner, visited) : null;
        }
        // 屏幕注册表的 screen 是路由身份，label 是显示值。只识别确实被映射为 screen → label 的表。
        var screenEntries = new HashSet<ObjectExpression>();
        var screenLabels = new List<Node>();
        var settingSectionSources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (node, function) in nodes)
        {
            if (node is not NewExpression { Callee: Identifier { Name: "Map" } } map || map.Arguments.Count != 1) continue;
            var items = map.Arguments[0] is ArrayExpression initializer ? initializer.Elements.OfType<Node>().ToArray() : [map.Arguments[0]];
            var mappedScreens = false;
            foreach (var itemExpression in items)
            {
                var expression = itemExpression is SpreadElement spread ? spread.Argument : itemExpression;
                if (expression is not CallExpression { Callee: MemberExpression { Property: Identifier { Name: "map" }, Object: Expression source } } select ||
                select.Arguments.Count != 1 || select.Arguments[0] is not ArrowFunctionExpression { Body: ArrayExpression pair } callback ||
                callback.Params.Count != 1 || callback.Params[0] is not Identifier item || pair.Elements.Count != 2 ||
                pair.Elements[0] is not MemberExpression { Object: Identifier keyObject, Property: Identifier { Name: "screen" } } ||
                pair.Elements[1] is not MemberExpression { Object: Identifier labelObject, Property: Identifier { Name: "label" } } ||
                keyObject.Name != item.Name || labelObject.Name != item.Name) continue;
                while (source is CallExpression { Callee: MemberExpression { Object: Expression collection, Property: Identifier { Name: "filter" } } }) source = collection;
                if (source is not Identifier registry || Owner(function, registry.Name) is not { } scope ||
                !declarations.TryGetValue((scope, registry.Name), out var value) || value is not ArrayExpression array ||
                array.Elements.Any(e => e is not ObjectExpression)) continue;
                mappedScreens = true;
                foreach (var entry in array.Elements.OfType<ObjectExpression>())
                    if (entry.Properties.All(p => p is ObjectProperty { Computed: false }) && entry.Properties.OfType<ObjectProperty>().Any(p => PropertyName(p) == "screen" && p.Value is StringLiteral))
                        screenEntries.Add(entry);
            }
            foreach (var pair in items.OfType<ArrayExpression>().Where(p => p.Elements.Count == 2 && p.Elements[0] is StringLiteral))
            {
                if (mappedScreens && pair.Elements[1] is StringLiteral label) screenLabels.Add(label);
                // 分组 title 也用于查找和筛选；这里只登记原文，翻译留到显示消费者。
                if (pair.Elements[1] is not ObjectExpression page ||
                    page.Properties.OfType<ObjectProperty>().FirstOrDefault(p => PropertyName(p) == "sections")?.Value is not ArrayExpression sections) continue;
                foreach (var section in sections.Elements.OfType<ObjectExpression>())
                    if (section.Properties.OfType<ObjectProperty>().Any(p => PropertyName(p) == "title" && p.Value is StringLiteral) &&
                        section.Properties.OfType<ObjectProperty>().Any(p => PropertyName(p) == "settings" && p.Value is ArrayExpression))
                        settingSectionSources.Add(((StringLiteral)section.Properties.OfType<ObjectProperty>().First(p => PropertyName(p) == "title").Value).Value);
            }
        }

        foreach (var (node, function) in nodes)
            if (node is CallExpression { Callee: MemberExpression { Property: Identifier { Name: "map" }, Object: Expression collection } } call &&
                call.Arguments.Count > 0 && call.Arguments[0] is ArrowFunctionExpression callback && callback.Params.Count > 0 && callback.Params[0] is Identifier item)
                callbacks[(callback.Start, item.Name)] = (collection, function);

        var settingSectionRows = new HashSet<(int Function, string Name)>();
        foreach (var binding in callbacks)
        {
            Expression source = binding.Value.Collection;
            while (source is CallExpression { Callee: MemberExpression { Object: Expression collection, Property: Identifier { Name: "filter" } } }) source = collection;
            if (source is not MemberExpression { Computed: false, Property: Identifier { Name: "sections" } }) continue;
            var callback = nodes.Select(p => p.Node).First(n => n.Start == binding.Key.Function && IsFunction(n));
            if (Walk(callback).Select(p => p.Node).OfType<MemberExpression>().Any(m => !m.Computed &&
                m.Object is Identifier item && item.Name == binding.Key.Name && m.Property is Identifier { Name: "settings" })) settingSectionRows.Add(binding.Key);
        }
        foreach (var producer in nodes.Select(p => p.Node).Where(IsFunction))
        foreach (var parameter in Parameters(producer).OfType<ObjectPattern>())
        {
            var fields = parameter.Properties.OfType<AssignmentProperty>().Where(p => !p.Computed && p.Key is Identifier && p.Value is Identifier)
                .ToDictionary(p => ((Identifier)p.Key).Name, p => ((Identifier)p.Value).Name);
            if (fields.ContainsKey("sectionTitle") && fields.TryGetValue("screen", out var screen) &&
                Walk(producer).Select(p => p.Node).OfType<MemberExpression>().Any(m => !m.Computed && m.Property is Identifier { Name: "sections" }))
                settingSectionRows.Add((producer.Start, screen));
        }

        List<Expression>? Expand(Expression expression, int function, HashSet<(int, string)> visiting)
        {
            List<Expression>? Merge(IEnumerable<Expression> values, int scope)
            {
                var result = new List<Expression>();
                foreach (var value in values) { var resolved = Expand(value, scopes.GetValueOrDefault(value, scope), new(visiting)); if (resolved is null) return null; result.AddRange(resolved); }
                return result;
            }
            switch (expression)
            {
                case Identifier id:
                    var owner = Owner(function, id.Name); if (owner is null || !visiting.Add((owner.Value, id.Name))) return null;
                    if (callbacks.TryGetValue((owner.Value, id.Name), out var binding))
                    {
                        var collections = Expand(binding.Collection, binding.Outer, visiting);
                        if (collections is null || collections.Any(v => v is not ArrayExpression || ((ArrayExpression)v).Elements.Any(e => e is null or SpreadElement))) return null;
                        return Merge(collections.Cast<ArrayExpression>().SelectMany(a => a.Elements.OfType<Expression>()), binding.Outer);
                    }
                    if (!fixedBindings.Contains((owner.Value, id.Name)))
                    {
                        // 只采用同一函数顶层、读取之前最后一次顺序写入；闭包、分支、循环和字段写入不推断。
                        if (owner.Value != function || !linearReads.Contains(id.Start) || ambiguous.Contains((function, id.Name)) || !writes.TryGetValue((function, id.Name), out var changes) || changes.Any(c => c.Closure)) return null;
                        var latest = changes.Where(c => c.Position < id.Start).OrderBy(c => c.Position).LastOrDefault();
                        if (latest.Position != 0 && !latest.Linear) return null;
                        if (latest.Value is not null) return Expand(latest.Value, function, visiting);
                    }
                    if (declarations.TryGetValue((owner.Value, id.Name), out var local)) return Expand(local, owner.Value, visiting);
                    if (destructured.TryGetValue((owner.Value, id.Name), out var picked))
                    {
                        var objects = Expand(picked.Value, owner.Value, visiting);
                        if (objects is null || objects.Any(v => v is not ObjectExpression obj || obj.Properties.Any(p => p is not ObjectProperty property || property.Computed))) return null;
                        var properties = objects.Cast<ObjectExpression>().Select(o => o.Properties.OfType<ObjectProperty>().LastOrDefault(p => PropertyName(p) == picked.Property)).ToArray();
                        if (properties.Any(p => p?.Value is not Expression)) return null;
                        return Merge(properties.Select(p => (Expression)p!.Value), owner.Value);
                    }
                    return null;
                case ChainExpression chain: return Expand(chain.Expression, function, visiting);
                case LogicalExpression logical: return Merge([logical.Left, logical.Right], function);
                case ConditionalExpression conditional:
                    return Merge([conditional.Consequent, conditional.Alternate], function);
                case TemplateLiteral template:
                {
                    var combinations = new List<string> { template.Quasis[0].Value.Cooked ?? "" };
                    for (var i = 0; i < template.Expressions.Count; i++)
                    {
                        var parts = Expand(template.Expressions[i], function, new(visiting));
                        if (parts is null || parts.Any(p => p is not StringLiteral) || combinations.Count * parts.Count > 32) return null;
                        combinations = combinations.SelectMany(prefix => parts.Cast<StringLiteral>().Select(part => prefix + part.Value + template.Quasis[i + 1].Value.Cooked)).Distinct().ToList();
                    }
                    return combinations.Select(s => (Expression)new StringLiteral(s, Quote(s))).ToList();
                }
                case BinaryExpression { Operator: Operator.Addition } binary:
                {
                    var left = Expand(binary.Left, function, new(visiting)); var right = Expand(binary.Right, function, new(visiting));
                    if (left is null || right is null || left.Any(p => p is not StringLiteral) || right.Any(p => p is not StringLiteral) || left.Count * right.Count > 32) return null;
                    return left.Cast<StringLiteral>().SelectMany(l => right.Cast<StringLiteral>().Select(r => l.Value + r.Value)).Distinct()
                        .Select(s => (Expression)new StringLiteral(s, Quote(s))).ToList();
                }
                case MemberExpression member:
                {
                    var objects = Expand(member.Object, function, visiting); if (objects is null) return null;
                    var name = member.Computed ? (member.Property as StringLiteral)?.Value : (member.Property as Identifier)?.Name;
                    var selected = new List<Expression>();
                    foreach (var value in objects)
                    {
                        if (value is ArrayExpression array && member.Computed && !array.Elements.Any(e => e is null or SpreadElement)) selected.AddRange(array.Elements.OfType<Expression>());
                        else if (value is ObjectExpression obj)
                        {
                            if (obj.Properties.Any(p => p is not ObjectProperty property || property.Computed)) return null;
                            var properties = obj.Properties.OfType<ObjectProperty>().Where(p => name is null || PropertyName(p) == name).ToArray();
                            if (properties.Length == 0 || properties.Any(p => p.Computed || p.Value is not Expression)) return null;
                            selected.AddRange(properties.Select(p => (Expression)p.Value));
                        }
                        else return null;
                    }
                    return Merge(selected, function);
                }
                case CallExpression call:
                {
                    Expression callee = call.Callee; if (callee is SequenceExpression sequence) callee = sequence.Expressions[^1];
                    List<Expression>? Results(Node producer)
                    {
                        if (producer is ArrowFunctionExpression { Body: Expression result }) return Merge([result], producer.Start);
                        var returns = Walk(producer).Where(p => p.Function == producer.Start).Select(p => p.Node).OfType<ReturnStatement>().ToArray();
                        return returns.Length == 0 || returns.Any(r => r.Argument is null) ? null : Merge(returns.Select(r => r.Argument!), producer.Start);
                    }
                    if (callee is MemberExpression { Property: Identifier { Name: "useMemo" } } && call.Arguments.Count > 0 && IsFunction(call.Arguments[0]))
                        return Results(call.Arguments[0]);
                    if (callee is MemberExpression { Object: Expression collection, Property: Identifier method } && method.Name is "filter" or "slice" or "sort" or "toSorted" or "reverse" or "toReversed")
                    {
                        var values = Expand(collection, function, new(visiting));
                        return values?.All(v => v is ArrayExpression) == true ? values : null;
                    }
                    if (callee is Identifier name)
                    {
                        var callOwner = Owner(function, name.Name);
                        if (callOwner is null || !fixedBindings.Contains((callOwner.Value, name.Name)) || !visiting.Add((callOwner.Value, "call:" + name.Name)) ||
                            !namedFunctions.TryGetValue((callOwner.Value, name.Name), out var producer)) return null;
                        return Results(producer);
                    }
                    if (callee is MemberExpression)
                    {
                        var functions = Expand(callee, function, new(visiting));
                        if (functions is null || functions.Any(f => !IsFunction(f))) return null;
                        var results = new List<Expression>();
                        foreach (var producer in functions) { var values = Results(producer); if (values is null) return null; results.AddRange(values); }
                        return results;
                    }
                    return null;
                }
                case StringLiteral or ObjectExpression or ArrayExpression or ArrowFunctionExpression or FunctionExpression: return [expression];
                default: return null;
            }
        }

        // 下拉框以英文显示名建立内部映射，汉化仅发生在最终标签、说明及提示处。
        const string optionLabel = "option-label", optionDescription = "option-description", optionTooltip = "option-tooltip";
        var optionSources = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        { [optionLabel] = new(StringComparer.Ordinal), [optionDescription] = new(StringComparer.Ordinal), [optionTooltip] = new(StringComparer.Ordinal) };
        var optionCallbacks = new Dictionary<(int Function, string Name), string>();
        var optionValues = new HashSet<(int Function, string Name)>();
        var optionMetadata = new HashSet<ObjectExpression>(ReferenceEqualityComparer.Instance);
        string? ResolverRole(string name) => name switch
        {
            "displayResolver" or "resolveOptionToString" or "resolveOptionToLabel" => optionLabel,
            "resolveOptionToDescription" => optionDescription, "getOptionTooltip" => optionTooltip, _ => null
        };
        void CollectOption(Expression value, int scope, string role)
        {
            switch (value)
            {
                case ConditionalExpression choice: CollectOption(choice.Consequent, scope, role); CollectOption(choice.Alternate, scope, role); break;
                case LogicalExpression logical: CollectOption(logical.Left, scope, role); CollectOption(logical.Right, scope, role); break;
                case ChainExpression chain: CollectOption(chain.Expression, scope, role); break;
                default:
                    var values = Expand(value, scope, []);
                    if (values is not null) foreach (var text in values.OfType<StringLiteral>())
                        if (LooksLikeText(text.Value)) optionSources[role].Add(text.Value);
                    break;
            }
        }
        IEnumerable<Expression> FunctionResults(Node producer) => producer is ArrowFunctionExpression { Body: Expression result }
            ? [result] : Walk(producer).Where(p => p.Function == producer.Start).Select(p => p.Node).OfType<ReturnStatement>().Select(r => r.Argument).OfType<Expression>();
        foreach (var (node, scope) in nodes)
        {
            if (desktop || IsProtected(node)) continue;
            if (IsFunction(node)) foreach (var parameter in Parameters(node).OfType<ObjectPattern>())
            {
                var fields = parameter.Properties.OfType<AssignmentProperty>().Where(p => !p.Computed && p.Key is Identifier && p.Value is Identifier)
                    .ToDictionary(p => ((Identifier)p.Key).Name, p => ((Identifier)p.Value).Name);
                if (!fields.ContainsKey("options") || !fields.ContainsKey("onSelect") || !fields.ContainsKey("displayResolver") && !fields.ContainsKey("resolveOptionToLabel")) continue;
                foreach (var (key, name) in fields) if (ResolverRole(key) is { } role) optionCallbacks[(node.Start, name)] = role;
                if (fields.ContainsKey("displayResolver") && fields.TryGetValue("value", out var selected)) optionValues.Add((node.Start, selected));
            }
            if (node is not ObjectExpression configuration) continue;
            var properties = configuration.Properties.OfType<ObjectProperty>().ToArray();
            var keys = properties.Select(PropertyName).ToHashSet(StringComparer.Ordinal);
            if (keys.IsSupersetOf(["displayName", "shortDescription", "longDescription"]) || keys.IsSupersetOf(["label", "description", "prefValues"]))
            {
                optionMetadata.Add(configuration);
                foreach (var property in properties)
                    if (PropertyName(property) is "displayName" or "shortDisplayName" or "label") CollectOption((Expression)property.Value, scope, optionLabel);
                    else if (PropertyName(property) is "shortDescription" or "longDescription" or "description") CollectOption((Expression)property.Value, scope, optionDescription);
            }
            if (!keys.Contains("options") || !keys.Contains("onSelect") && !keys.Contains("isProviderSetting") && !keys.Contains("settingKey")) continue;
            foreach (var property in properties)
            {
                if (ResolverRole(PropertyName(property)) is not { } role || property.Value is not Expression resolver) continue;
                var producers = IsFunction(resolver) ? [resolver] : Expand(resolver, scope, []) ?? [];
                if (resolver is Identifier name && Owner(scope, name.Name) is { } owner && fixedBindings.Contains((owner, name.Name)) && namedFunctions.TryGetValue((owner, name.Name), out var named))
                    foreach (var value in FunctionResults(named)) CollectOption(value, named.Start, role);
                foreach (var producer in producers.Where(IsFunction)) foreach (var value in FunctionResults(producer)) CollectOption(value, producer.Start, role);
            }
        }
        string? OptionRole(Expression expression, int scope, HashSet<(int, string)> visited)
        {
            if (expression is ChainExpression chain) return OptionRole(chain.Expression, scope, visited);
            if (expression is ConditionalExpression choice) return OptionRole(choice.Consequent, scope, new(visited)) ?? OptionRole(choice.Alternate, scope, visited);
            if (expression is LogicalExpression logical) return OptionRole(logical.Left, scope, new(visited)) ?? OptionRole(logical.Right, scope, visited);
            if (expression is MemberExpression { Computed: false, Property: Identifier field } member &&
                field.Name is "displayName" or "shortDisplayName" or "label" or "shortDescription" or "longDescription" or "description")
            {
                var objects = Expand(member.Object, scope, []);
                if (objects is { Count: > 0 } && objects.All(o => o is ObjectExpression metadata && optionMetadata.Contains(metadata)))
                    return field.Name is "displayName" or "shortDisplayName" or "label" ? optionLabel : optionDescription;
            }
            if (expression is CallExpression call)
            {
                var callee = call.Callee is SequenceExpression sequence ? sequence.Expressions[^1] : call.Callee;
                if (callee is Identifier callback && Owner(scope, callback.Name) is { } owner && fixedBindings.Contains((owner, callback.Name)) &&
                    optionCallbacks.TryGetValue((owner, callback.Name), out var role)) return role;
                if (callee is MemberExpression { Property: Identifier { Name: "useMemo" } } && call.Arguments.Count > 0 && IsFunction(call.Arguments[0]))
                    foreach (var value in FunctionResults(call.Arguments[0])) if (OptionRole(value, call.Arguments[0].Start, new(visited)) is { } result) return result;
                return null;
            }
            if (expression is not Identifier id || Owner(scope, id.Name) is not { } local || !visited.Add((local, id.Name))) return null;
            if (fixedBindings.Contains((local, id.Name)))
            {
                if (optionValues.Contains((local, id.Name))) return optionLabel;
                return declarations.TryGetValue((local, id.Name), out var initial) ? OptionRole(initial, local, visited) : null;
            }
            if (local != scope || ambiguous.Contains((local, id.Name)) || !writes.TryGetValue((local, id.Name), out var changes) || changes.Any(c => c.Closure)) return null;
            var latest = changes.Where(c => c.Position < id.Start).OrderBy(c => c.Position).LastOrDefault();
            return latest.Linear && latest.Value is not null ? OptionRole(latest.Value, local, visited) : null;
        }

        var lookupSeen = new HashSet<(int, string)>();
        var serviceSeen = new HashSet<(int, string)>();
        var activeFunction = -1;

        // 局部变量可混合默认标题与用户名称。只有后续读取全部流向显示值时，才翻译其固定分支。
        var displayReads = new HashSet<int>();
        void MarkDisplayReads(Node node, HashSet<int> destination)
        {
            switch (node)
            {
                case Identifier id: destination.Add(id.Start); break;
                case TemplateLiteral template: foreach (var value in template.Expressions) MarkDisplayReads(value, destination); break;
                case ConditionalExpression conditional: MarkDisplayReads(conditional.Consequent, destination); MarkDisplayReads(conditional.Alternate, destination); break;
                case LogicalExpression logical: MarkDisplayReads(logical.Left, destination); MarkDisplayReads(logical.Right, destination); break;
                case BinaryExpression { Operator: Operator.Addition } addition: MarkDisplayReads(addition.Left, destination); MarkDisplayReads(addition.Right, destination); break;
                case ArrayExpression array: foreach (var value in array.Elements) if (value is not null) MarkDisplayReads(value, destination); break;
            }
        }
        foreach (var call in nodes.Select(p => p.Node).OfType<CallExpression>().Where(c => !IsProtected(c)))
        {
            var callee = call.Callee is SequenceExpression sequence ? sequence.Expressions[^1] : call.Callee;
            if (callee is MemberExpression { Property: Identifier { Name: "createElement" or "jsx" or "jsxs" or "jsxDEV" } method } && call.Arguments.Count >= 2)
                foreach (var (value, _) in DisplayValues(call, method.Name)) MarkDisplayReads(value, displayReads);
        }
        var candidates = nodes.Select(p => p.Node).OfType<Identifier>().Where(id => displayReads.Contains(id.Start))
            .Select(id => (Function: Owner(scopes[id], id.Name), id.Name)).Where(p => p.Function is not null)
            .Select(p => (Function: p.Function!.Value, p.Name)).Distinct().Where(key => key.Function >= 0 &&
                (writes.ContainsKey(key) || fixedBindings.Contains(key) && declarations.ContainsKey(key)) && !ambiguous.Contains(key)).ToHashSet();
        var references = candidates.ToDictionary(key => key, _ => new List<int>());
        var plainAssignments = new HashSet<int>();
        var pendingReferences = new Stack<Node>(); pendingReferences.Push(tree);
        while (pendingReferences.TryPop(out var node))
        {
            if (node is Identifier id && Owner(scopes[id], id.Name) is { } scope && references.TryGetValue((scope, id.Name), out var uses)) uses.Add(id.Start);
            if (node is AssignmentExpression { Operator: Operator.Assignment, Left: Identifier } assignment)
            { plainAssignments.Add(node.Start); pendingReferences.Push(assignment.Right); }
            else if (node is MemberExpression member) { pendingReferences.Push(member.Object); if (member.Computed) pendingReferences.Push(member.Property); }
            else if (node is ObjectProperty property) { pendingReferences.Push(property.Value); if (property.Computed) pendingReferences.Push(property.Key); }
            else if (node is AssignmentProperty binding) { pendingReferences.Push(binding.Value); if (binding.Computed) pendingReferences.Push(binding.Key); }
            else foreach (var child in node.ChildNodes) pendingReferences.Push(child);
        }
        var displayAssignments = new List<(int Function, Expression Value)>();
        foreach (var key in candidates)
        {
            if (fixedBindings.Contains(key) && declarations.TryGetValue(key, out var initial))
            {
                // 已能完整解析的文字仍在消费处合并翻译，避免提前翻译名词破坏整句匹配。
                var resolved = Expand(initial, key.Function, []);
                if ((resolved is null || resolved.Any(value => value is not StringLiteral)) &&
                    references[key].All(position => position < initial.End || displayReads.Contains(position)))
                    displayAssignments.Add((key.Function, initial));
                continue;
            }
            var changes = writes[key].OrderBy(c => c.Position).ToArray();
            var first = Array.FindIndex(changes, c => c.Linear && plainAssignments.Contains(c.Position) && c.Value is not null && Walk(c.Value).Any(p => p.Node is StringLiteral text && LooksLikeText(text.Value)));
            if (first < 0 || changes.Any(c => c.Closure) || changes.Skip(first).Any(c => !c.Linear || !plainAssignments.Contains(c.Position) || c.Value is null)) continue;
            var allowed = new HashSet<int>(displayReads);
            foreach (var change in changes.Skip(first)) MarkDisplayReads(change.Value!, allowed);
            if (references[key].Any(position => position >= changes[first].End && !allowed.Contains(position))) continue;
            displayAssignments.AddRange(changes.Skip(first).Select(c => (key.Function, c.Value!)));
        }

        void FinishGroup(string kind, List<UiText> texts)
        {
            if (texts.Count == 0) return;
            var scope = desktop ? "desktop" : "app-ui";
            var signature = kind + "\n" + string.Join('\n', texts.Select(t => t.Role + "\0" + t.Shape + "\0" + t.Source));
            var groupKey = JsonFiles.Hash(scope + "\0" + signature);
            groups.Add(new(groupKey, kind, texts.Select(t => t with
            {
                GroupKey = groupKey,
                Key = JsonFiles.Hash(scope + "\0" + signature + "\0" + t.Role + "\0" + t.Shape + "\0" + t.Source)
            }).ToList()));
        }

        void Group(Node owner, string kind, IEnumerable<(Node Node, string Role)> values)
        {
            var texts = new List<UiText>();
            void Add(Node node, string source, string shape, string role, List<ExpressionSlot>? slots = null)
            {
                source = source.Replace("\r\n", "\n").Replace('\r', '\n');
                if (!LooksLikeText(source) || !seen.Add(node.Start)) return;
                texts.Add(new("", "", file, node.Start, node.End, source, shape, role, slots ?? []));
            }
            void Value(Node node, string role)
            {
                if (node is MemberExpression { Computed: false, Object: Identifier section, Property: Identifier { Name: "title" } } &&
                    Owner(activeFunction, section.Name) is { } sectionScope && settingSectionRows.Contains((sectionScope, section.Name)))
                {
                    foreach (var source in settingSectionSources.Order(StringComparer.Ordinal))
                        if (lookupSeen.Add((node.Start, source))) texts.Add(new("", "", file, node.Start, node.End, source, "lookup", "section-title", []));
                    return;
                }
                if (node is Expression option && node is Identifier or MemberExpression or CallExpression or ChainExpression &&
                    OptionRole(option, activeFunction, []) is { } optionRole && optionSources[optionRole].Count > 0)
                {
                    foreach (var source in optionSources[optionRole].Order(StringComparer.Ordinal))
                        if (lookupSeen.Add((node.Start, source))) texts.Add(new("", "", file, node.Start, node.End, source, "lookup", optionRole, []));
                    return;
                }
                if (node is Expression display && ServiceRole(display, activeFunction, []) is { } serviceRole)
                {
                    foreach (var source in ServiceDisplay.Sources(serviceRole))
                        if (serviceSeen.Add((node.Start, source))) texts.Add(new("", "", file, node.Start, node.End, source, "service", serviceRole, []));
                    return;
                }
                if (node is CallExpression { Callee: Identifier formatter } formatted && formatted.Arguments.Count >= 2 &&
                    Owner(activeFunction, formatter.Name) is { } formatterScope && fixedBindings.Contains((formatterScope, formatter.Name)) &&
                    namedFunctions.TryGetValue((formatterScope, formatter.Name), out var producer) && Parameters(producer).FirstOrDefault() is Identifier environment &&
                    Walk(producer).Select(p => p.Node).OfType<SwitchStatement>().Any(choice =>
                        choice.Discriminant is MemberExpression { Computed: false, Object: Identifier selected, Property: Identifier { Name: "type" } } && selected.Name == environment.Name &&
                        choice.Cases.Any(c => c.Test is StringLiteral { Value: "sot" }) && choice.Cases.Any(c => c.Test is StringLiteral { Value: "environment" })))
                {
                    foreach (var source in new[] { "Local", "New Workspace", "New Worktree" })
                        if (lookupSeen.Add((node.Start, source))) texts.Add(new("", "", file, node.Start, node.End, source, "environment-label", "children", []));
                    return;
                }
                switch (node)
                {
                    case CallExpression memo when kind == "ui/display-local" &&
                        memo.Callee is MemberExpression { Property: Identifier { Name: "useMemo" } } or SequenceExpression &&
                        (memo.Callee is not SequenceExpression memoSequence || memoSequence.Expressions[^1] is MemberExpression { Property: Identifier { Name: "useMemo" } }) &&
                        memo.Arguments.Count > 0 && IsFunction(memo.Arguments[0]):
                        foreach (var result in FunctionResults(memo.Arguments[0])) Value(result, role);
                        break;
                    case StringLiteral literal: Add(literal, literal.Value, "string", role); break;
                    case TemplateLiteral template:
                    {
                        var text = new StringBuilder(); var slots = new List<ExpressionSlot>();
                        for (var i = 0; i < template.Quasis.Count; i++)
                        {
                            text.Append(template.Quasis[i].Value.Cooked);
                            if (i < template.Expressions.Count)
                            {
                                text.Append($"⟪AGC{i}⟫");
                                var expression = template.Expressions[i];
                                var plural = expression is ConditionalExpression pluralChoice &&
                                    (pluralChoice.Consequent is StringLiteral { Value: "s" } && pluralChoice.Alternate is StringLiteral { Value: "" } ||
                                     pluralChoice.Consequent is StringLiteral { Value: "" } && pluralChoice.Alternate is StringLiteral { Value: "s" }) &&
                                    template.Quasis[i].Value.Cooked is { Length: > 0 } prefix && char.IsAsciiLetter(prefix[^1]);
                                slots.Add(new(expression.Start, expression.End, plural));
                                Value(expression, role);
                            }
                        }
                        Add(template, text.ToString(), "template", role, slots); break;
                    }
                    case ConditionalExpression conditional:
                        Value(conditional.Consequent, role); Value(conditional.Alternate, role); break;
                    case LogicalExpression logical: Value(logical.Right, role); break;
                    case BinaryExpression { Operator: Operator.Addition } binary:
                        // 只改拼接中的固定文字，保留 + 运算、分组和原来的隐式类型转换。
                        Value(binary.Left, role); Value(binary.Right, role); break;
                    case ArrayExpression array:
                        foreach (var element in array.Elements) if (element is not null) Value(element, role);
                        break;
                    case Expression expression when node is Identifier or MemberExpression or CallExpression:
                    {
                        var values = Expand(expression, activeFunction, []);
                        if (values is null || values.Any(v => v is not StringLiteral)) break;
                        foreach (var literal in values.OfType<StringLiteral>().DistinctBy(v => v.Value))
                            if (LooksLikeText(literal.Value) && lookupSeen.Add((node.Start, literal.Value)))
                                texts.Add(new("", "", file, node.Start, node.End, literal.Value, "lookup", role, []));
                        break;
                    }
                }
            }
            foreach (var (node, role) in values) Value(node, role);
            FinishGroup(kind, texts);
        }

        bool GroupTextChildren(CallExpression call, string kind)
        {
            if (call.Arguments.Count < 4 || call.Arguments[0] is not StringLiteral ||
                call.Arguments[1] is ObjectExpression props && props.Properties.OfType<ObjectProperty>().Any(p => PropertyName(p) is "ref" or "contentEditable" or "dangerouslySetInnerHTML")) return false;
            var children = call.Arguments.Skip(2).ToArray();
            if (children.All(c => c is StringLiteral) || !children.OfType<StringLiteral>().Any(s => s.Value.Length > 0 &&
                (char.IsWhiteSpace(s.Value[0]) || char.IsWhiteSpace(s.Value[^1])))) return false;
            var combinations = new List<string> { "" };
            foreach (var child in children)
            {
                if (child is not Expression value) return false;
                var parts = Expand(value, activeFunction, []);
                if (parts is null || parts.Count == 0 || parts.Any(p => p is not StringLiteral) || combinations.Count * parts.Count > 32) return false;
                combinations = combinations.SelectMany(prefix => parts.Cast<StringLiteral>().Select(part => prefix + part.Value)).Distinct().ToList();
            }
            if (!combinations.Any(LooksLikeText)) return false;
            var start = call.Arguments[2].Start; var end = call.Arguments[^1].End;
            FinishGroup(kind, combinations.Where(LooksLikeText).Select(source => new UiText("", "", file, start, end, source, "children", "children", [])).ToList());
            return true;
        }

        foreach (var assignment in displayAssignments)
        {
            activeFunction = assignment.Function;
            Group(assignment.Value, "ui/display-local", [(assignment.Value, "children")]);
        }
        foreach (var label in screenLabels) Group(label, "ui/setting-screen", [(label, "label")]);

        foreach (var (node, function) in nodes)
        {
            activeFunction = function;
            if (IsProtected(node)) continue;
            // 只处理 ContextScopeItem 的系统斜杠命令说明，不改模型指令或自定义技能正文。
            if (!desktop && node is NewExpression commandItem && commandItem.Arguments.Count >= 4 &&
                commandItem.Arguments[0] is CallExpression scopeCall && scopeCall.Arguments.Count >= 2 &&
                scopeCall.Arguments[0] is MemberExpression { Property: Identifier { Name: "ContextScopeItemSchema" } } &&
                scopeCall.Arguments[1] is ObjectExpression scopeObject &&
                scopeObject.Properties.OfType<ObjectProperty>().FirstOrDefault(p => PropertyName(p) == "scopeItem")?.Value is ObjectExpression scopeItem &&
                scopeItem.Properties.OfType<ObjectProperty>().Any(p => PropertyName(p) == "case" && p.Value is StringLiteral { Value: "slashCommand" }) &&
                scopeItem.Properties.OfType<ObjectProperty>().FirstOrDefault(p => PropertyName(p) == "value")?.Value is ObjectExpression scopeValue &&
                scopeValue.Properties.OfType<ObjectProperty>().FirstOrDefault(p => PropertyName(p) == "info")?.Value is MemberExpression { Computed: false, Object: Identifier owner, Property: Identifier { Name: "info" } } &&
                commandItem.Arguments[3] is MemberExpression { Computed: false, Object: Identifier descriptionOwner, Property: Identifier { Name: "description" } } description &&
                owner.Name == descriptionOwner.Name)
                FinishGroup("ui/system-command", ServiceDisplay.Sources(ServiceDisplay.CommandDescription).Select(source =>
                    new UiText("", "", file, description.Start, description.End, source, "system-command", ServiceDisplay.CommandDescription, [])).ToList());
            if (!desktop && node is ObjectExpression configuration)
            {
                var properties = configuration.Properties.OfType<ObjectProperty>().ToArray();
                var keys = properties.Select(PropertyName).ToHashSet(StringComparer.Ordinal);
                // 通用选项组件先生成 value/label 记录；只转换已确认显示解析器产生的标签和提示。
                if (keys.Contains("value") && properties.FirstOrDefault(p => PropertyName(p) == "label")?.Value is Expression optionText &&
                    OptionRole(optionText, function, []) is not null)
                    Group(configuration, "ui/option-display", properties.Where(p => PropertyName(p) is "label" or "tooltip" && p.Key.Start != p.Value.Start)
                        .Select(p => ((Node)p.Value, PropertyName(p))));
                var command = keys.IsSupersetOf(["id", "label", "keybindings"]);
                var setting = keys.IsSupersetOf(["isProviderSetting", "settingType", "label"]);
                var navigation = keys.IsSupersetOf(["sidebarLabel", "searchPlaceholder"]);
                var sectionHeader = keys.IsSupersetOf(["title", "headerActions"]);
                var isMenuConfig = keys.Contains("label") && properties.Any(p => PropertyName(p) is "onClick" or "onSelect" &&
                    p.Value is ArrowFunctionExpression or FunctionExpression or Identifier or MemberExpression or ChainExpression);
                if (command || setting || navigation || isMenuConfig || sectionHeader)
                    Group(configuration, command ? "ui/command" : setting ? "ui/setting" : navigation ? "ui/navigation" : sectionHeader ? "ui/section-header" : "ui/menu",
                        properties.Where(p => TextProperties.Contains(PropertyName(p)) || PropertyName(p) is "sidebarLabel" or "searchPlaceholder" or "emptyMessageSingular" or "emptyMessagePlural")
                            .Select(p => ((Node)p.Value, PropertyName(p))));
                if (setting && properties.FirstOrDefault(p => PropertyName(p) == "resolveOptionToString")?.Value is Node resolver && IsFunction(resolver))
                {
                    activeFunction = resolver.Start;
                    if (resolver is ArrowFunctionExpression { Body: Expression result }) Group(result, "ui/setting-option", [(result, "label")]);
                    else foreach (var returned in Walk(resolver).Where(p => p.Function == resolver.Start).Select(p => p.Node).OfType<ReturnStatement>().Where(r => r.Argument is not null))
                        Group(returned, "ui/setting-option", [(returned.Argument!, "label")]);
                    activeFunction = function;
                }
                if (screenEntries.Contains(configuration))
                {
                    var label = properties.LastOrDefault(p => PropertyName(p) == "label");
                    if (label is not null) Group(configuration, "ui/setting-screen", [(label.Value, "label")]);
                    else if (properties.Last(p => PropertyName(p) == "screen").Value is StringLiteral screen && LooksLikeText(screen.Value))
                    {
                        var insertion = configuration.Properties[^1].End;
                        if (seen.Add(insertion)) FinishGroup("ui/setting-screen", [new("", "", file, insertion, insertion, screen.Value, "screen-label", "label", [])]);
                    }
                }
            }
            if (!desktop && IsFunction(node) && Parameters(node).OfType<ObjectPattern>().Any(pattern =>
                    pattern.Properties.OfType<AssignmentProperty>().Select(p => p.Key).OfType<Identifier>().Select(p => p.Name).ToHashSet().IsSupersetOf(["inputPlaceholder", "supportsWorkflows"])))
                foreach (var returned in Walk(node).Where(p => p.Function == node.Start).Select(p => p.Node).OfType<ReturnStatement>().Where(r => r.Argument is not null))
                    Group(returned, "ui/input-placeholder", [(returned.Argument!, "placeholder")]);
            if (node is CallExpression call)
            {
                Expression callee = call.Callee;
                if (callee is SequenceExpression sequence) callee = sequence.Expressions[^1];
                if (callee is MemberExpression { Property: Identifier renderMethod } &&
                    renderMethod.Name is "createElement" or "jsx" or "jsxs" or "jsxDEV" && call.Arguments.Count >= 2)
                {
                    var values = DisplayValues(call, renderMethod.Name);
                    var tag = call.Arguments[0] is StringLiteral name ? name.Value : "component";
                    if (tag is not ("style" or "script" or "code" or "pre" or "kbd" or "textarea") &&
                        (tag != "option" || call.Arguments[1] is ObjectExpression option && option.Properties.OfType<ObjectProperty>().Any(p => PropertyName(p) == "value")))
                    {
                        if (renderMethod.Name == "createElement" && GroupTextChildren(call, "ui/" + tag)) values.RemoveAll(p => p.Role == "children");
                        Group(call, "ui/" + tag, values);
                    }
                }
                if (desktop && callee is MemberExpression { Property: Identifier method })
                {
                    if (method.Name is "showMessageBox" or "showMessageBoxSync")
                    {
                        foreach (var argument in call.Arguments)
                        {
                            Node? options = argument;
                            Expression? resolved = null;
                            if (argument is Identifier variable) declarations.TryGetValue((function, variable.Name), out resolved);
                            options = resolved ?? options;
                            if (options is ObjectExpression obj)
                                Group(obj, "dialog", obj.Properties.OfType<ObjectProperty>()
                                    .Where(p => TextProperties.Contains(PropertyName(p)) || PropertyName(p) == "buttons")
                                    .Select(p => ((Node)p.Value, PropertyName(p))));
                        }
                    }
                    else if (method.Name == "showErrorBox")
                        Group(call, "dialog/error", call.Arguments.Select(a => ((Node)a, "message")));
                    else if (method.Name == "setToolTip" && file.EndsWith("tray.js", StringComparison.Ordinal))
                        Group(call, "tray", call.Arguments.Select(a => ((Node)a, "tooltip")));
                }
            }
            // 菜单和托盘模板的 label 是 Electron 显示槽，role、id、accelerator 等身份字段不进入提取。
            if (desktop && (file.EndsWith("menu.js", StringComparison.Ordinal) || file.EndsWith("tray.js", StringComparison.Ordinal)) &&
                node is ObjectExpression menu)
                Group(menu, "menu", menu.Properties.OfType<ObjectProperty>().Where(p => PropertyName(p) == "label")
                    .Select(p => ((Node)p.Value, "label")));
        }
        return groups;
    }

    private static IEnumerable<(Node Node, int Function)> Walk(Node root)
    {
        var pending = new Stack<(Node, int)>(); pending.Push((root, -1));
        while (pending.TryPop(out var pair))
        {
            var (node, function) = pair;
            if (node is FunctionDeclaration or FunctionExpression or ArrowFunctionExpression) function = node.Start;
            yield return (node, function);
            var children = node.ChildNodes.ToArray();
            for (var i = children.Length - 1; i >= 0; i--) pending.Push((children[i], function));
        }
    }

    private static bool IsFunction(Node node) => node is FunctionDeclaration or FunctionExpression or ArrowFunctionExpression;
    private static List<(Node Node, string Role)> DisplayValues(CallExpression call, string method)
    {
        var values = new List<(Node, string)>();
        if (call.Arguments[1] is ObjectExpression props)
            foreach (var property in props.Properties.OfType<ObjectProperty>())
            {
                var key = PropertyName(property);
                if (key == "text" && props.Properties.OfType<ObjectProperty>().Any(other => PropertyName(other) == "label")) continue;
                if (TextProperties.Contains(key) || key == "children") values.Add((property.Value, key));
            }
        if (method == "createElement") for (var i = 2; i < call.Arguments.Count; i++) values.Add((call.Arguments[i], "children"));
        return values;
    }
    private static IEnumerable<Node> Parameters(Node node) => node switch
    {
        FunctionDeclaration f => f.Params.Cast<Node>(), FunctionExpression f => f.Params.Cast<Node>(),
        ArrowFunctionExpression f => f.Params.Cast<Node>(), _ => []
    };
    private static IEnumerable<string> BindingNames(Node pattern)
    {
        if (pattern is Identifier id) { yield return id.Name; yield break; }
        var children = pattern switch { AssignmentProperty p => new[] { p.Value }, AssignmentPattern p => new[] { p.Left }, _ => pattern.ChildNodes.ToArray() };
        foreach (var child in children) foreach (var name in BindingNames(child)) yield return name;
    }

    private static bool LooksLikeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !English().IsMatch(text) || text.Contains('\0')) return false;
        var s = Slots().Replace(text, "").Trim();
        if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase) || s.StartsWith('/') || s.Contains(":\\", StringComparison.Ordinal) ||
            s.StartsWith("$ ", StringComparison.Ordinal) || s.StartsWith("#!/", StringComparison.Ordinal)) return false;
        if (Command().IsMatch(s)) return false;
        if (s.Contains(' ') || s.Any(char.IsUpper)) return true;
        return s is "settings" or "cancel" or "close" or "save" or "apply" or "remove" or "open" or "copy" or "view" or
            "edit" or "search" or "enabled" or "disabled" or "running" or "stopped" or "yes" or "no" or "name" or "status" or
            "size" or "created" or "actions" or "permissions" or "delete" or "start" or "stop" or "run" or "and" or "or" or
            "project" or "projects" or "workspace" or "workspaces" or "resource" or "resources" or "folder" or "folders";
    }

    public static string Apply(string code, IReadOnlyList<UiText> texts, IReadOnlyDictionary<string, string> translations)
    {
        string Range(int start, int end)
        {
            var output = new StringBuilder(); var position = start;
            foreach (var text in texts.Where(t => t.Start >= start && t.End <= end && translations.ContainsKey(t.Key))
                         .OrderBy(t => t.Start).ThenByDescending(t => t.End))
            {
                if (text.Start < position) continue;
                output.Append(code.AsSpan(position, text.Start - position)); output.Append(Render(text)); position = text.End;
            }
            output.Append(code.AsSpan(position, end - position)); return output.ToString();
        }
        string Render(UiText text)
        {
            if (text.Shape == "environment-label")
            {
                var expression = code[text.Start..text.End];
                var call = Walk(Parse(expression)).Select(p => p.Node).OfType<CallExpression>().First();
                var labels = texts.Where(t => t.Start == text.Start && t.End == text.End && t.Shape == text.Shape && translations.ContainsKey(t.Key))
                    .DistinctBy(t => t.Source).ToDictionary(t => t.Source, t => translations[t.Key]);
                var choices = string.Join("", labels.Select(pair => $"value==={Quote(pair.Key)}?{Quote(pair.Value)}:")) + "value";
                var arguments = string.Join(',', call.Arguments.Select(a => expression[a.Start..a.End]));
                return $"((format,...args)=>{{const value=format(...args);return args[0]?.type==='sot'||args[0]?.type==='copy'?({choices}):value;}})({expression[call.Callee.Start..call.Callee.End]},{arguments})";
            }
            if (text.Shape == "system-command")
            {
                var expression = code[text.Start..text.End];
                var owner = (Identifier)Walk(Parse(expression)).Select(p => p.Node).OfType<MemberExpression>().Single().Object;
                var descriptions = texts.Where(t => t.Start == text.Start && t.End == text.End && t.Shape == text.Shape && translations.ContainsKey(t.Key))
                    .DistinctBy(t => t.Source).ToDictionary(t => t.Source, t => translations[t.Key]);
                return $"((command)=>command.info?.type===1?{ServiceDisplay.Render("command.description", descriptions)}:command.description)({owner.Name})";
            }
            if (text.Shape == "service")
                return ServiceDisplay.Render(code[text.Start..text.End], texts.Where(t =>
                    t.Start == text.Start && t.End == text.End && t.Shape == "service" && translations.ContainsKey(t.Key))
                    .DistinctBy(t => t.Source).ToDictionary(t => t.Source, t => translations[t.Key]));
            if (text.Shape == "screen-label")
            {
                return ",label:" + Quote(translations[text.Key]);
            }
            if (text.Shape is "lookup" or "children")
            {
                var choices = texts.Where(t => t.Start == text.Start && t.End == text.End && t.Shape == text.Shape && translations.ContainsKey(t.Key))
                    .DistinctBy(t => t.Source).ToArray();
                // 在显示调用处转换值；生产它的配置、标识符和业务比较保持原样，表达式只求值一次。
                var body = string.Join("", choices.Select(t => $"value==={Quote(t.Source)}?{Quote(translations[t.Key])}:")) + "value";
                return text.Shape == "children" ? $"((values)=>{{const value=values.join('');return {body};}})([{code[text.Start..text.End]}])" :
                    $"((value)=>{body})({code[text.Start..text.End]})";
            }
            var translation = translations[text.Key];
            var expected = Enumerable.Range(0, text.Slots.Count).Select(i => $"⟪AGC{i}⟫");
            // 保持表达式原有求值顺序，避免翻译改变函数调用等副作用。
            if (!expected.SequenceEqual(Slots().Matches(translation).Select(m => m.Value)))
                throw new InvalidDataException($"动态内容的数量或顺序发生变化：{text.File}");
            if (text.Shape == "string") return Quote(translation);
            var output = new StringBuilder("`"); var position = 0;
            foreach (Match slot in Slots().Matches(translation))
            {
                output.Append(EscapeTemplate(translation[position..slot.Index]));
                var expression = text.Slots[int.Parse(slot.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)];
                var value = Range(expression.Start, expression.End);
                // 英文名词的单复数词尾不进入中文；仍求值一次，保留条件及其副作用。
                if (expression.IsPluralSuffix) value = $"((suffix)=>suffix==='s'?'':suffix)({value})";
                output.Append("${").Append(value).Append('}');
                position = slot.Index + slot.Length;
            }
            return output.Append(EscapeTemplate(translation[position..])).Append('`').ToString();
        }
        var patched = Range(0, code.Length); Validate(patched); return patched;
    }

    private static string EscapeTemplate(string text) => text.Replace("\\", "\\\\").Replace("`", "\\`")
        .Replace("${", "\\${").Replace("\r", "\\r").Replace("\n", "\\n");
    [GeneratedRegex("[A-Za-z]{2}")] private static partial Regex English();
    [GeneratedRegex(@"\.\s*(?:createElement|jsx|jsxs|jsxDEV)\b")] private static partial Regex UiCalls();
    [GeneratedRegex(@"^(?:git|docker|npm|pnpm|npx|node|python|uv|cargo|go|kubectl|curl|ssh|cd)\s+")] private static partial Regex Command();
    [GeneratedRegex("⟪AGC(\\d+)⟫")] public static partial Regex Slots();
}
