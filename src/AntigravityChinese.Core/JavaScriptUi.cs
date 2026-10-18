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
        "displayText", "subtitle", "subTitle", "primaryCtaText", "secondaryCtaText", "ariaLabel"
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
        var writes = new Dictionary<(int Function, string Name), List<(int Position, Expression? Value, bool Linear, bool Closure)>>();
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
                values.Add((node.Start, node is AssignmentExpression assignment ? assignment.Right : null, direct && scope == function && linear.Contains(node.Start), scope != function));
            }
        }
        var fixedBindings = bindings.Except(assigned).Except(ambiguous).ToHashSet();
        // 屏幕注册表的 screen 是路由身份，label 是显示值。只识别确实被映射为 screen → label 的表。
        var screenEntries = new HashSet<ObjectExpression>();
        foreach (var (node, function) in nodes)
        {
            if (node is not NewExpression { Callee: Identifier { Name: "Map" } } map || map.Arguments.Count != 1 ||
                map.Arguments[0] is not CallExpression { Callee: MemberExpression { Property: Identifier { Name: "map" }, Object: Expression source } } select ||
                select.Arguments.Count != 1 || select.Arguments[0] is not ArrowFunctionExpression { Body: ArrayExpression pair } callback ||
                callback.Params.Count != 1 || callback.Params[0] is not Identifier item || pair.Elements.Count != 2 ||
                pair.Elements[0] is not MemberExpression { Object: Identifier keyObject, Property: Identifier { Name: "screen" } } ||
                pair.Elements[1] is not MemberExpression { Object: Identifier labelObject, Property: Identifier { Name: "label" } } ||
                keyObject.Name != item.Name || labelObject.Name != item.Name) continue;
            while (source is CallExpression { Callee: MemberExpression { Object: Expression collection, Property: Identifier { Name: "filter" } } }) source = collection;
            if (source is not Identifier registry || Owner(function, registry.Name) is not { } scope ||
                !declarations.TryGetValue((scope, registry.Name), out var value) || value is not ArrayExpression array ||
                array.Elements.Any(e => e is not ObjectExpression)) continue;
            foreach (var entry in array.Elements.OfType<ObjectExpression>())
                if (entry.Properties.All(p => p is ObjectProperty { Computed: false }) && entry.Properties.OfType<ObjectProperty>().Any(p => PropertyName(p) == "screen" && p.Value is StringLiteral))
                    screenEntries.Add(entry);
        }

        foreach (var (node, function) in nodes)
            if (node is CallExpression { Callee: MemberExpression { Property: Identifier { Name: "map" }, Object: Expression collection } } call &&
                call.Arguments.Count > 0 && call.Arguments[0] is ArrowFunctionExpression callback && callback.Params.Count > 0 && callback.Params[0] is Identifier item)
                callbacks[(callback.Start, item.Name)] = (collection, function);

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

        var lookupSeen = new HashSet<(int, string)>();
        var activeFunction = -1;

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
                switch (node)
                {
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
                                slots.Add(new(template.Expressions[i].Start, template.Expressions[i].End));
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

        foreach (var (node, function) in nodes)
        {
            activeFunction = function;
            if (IsProtected(node)) continue;
            if (!desktop && node is ObjectExpression configuration)
            {
                var properties = configuration.Properties.OfType<ObjectProperty>().ToArray();
                var keys = properties.Select(PropertyName).ToHashSet(StringComparer.Ordinal);
                var command = keys.IsSupersetOf(["id", "label", "keybindings"]);
                var setting = keys.IsSupersetOf(["isProviderSetting", "settingType", "label"]);
                var navigation = keys.IsSupersetOf(["sidebarLabel", "searchPlaceholder"]);
                var sectionHeader = keys.IsSupersetOf(["title", "headerActions"]);
                var isMenuConfig = keys.Contains("label") && properties.Any(p => PropertyName(p) is "onClick" or "onSelect" && p.Value is ArrowFunctionExpression or FunctionExpression);
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
                    var values = new List<(Node, string)>();
                    if (call.Arguments[1] is ObjectExpression props)
                        foreach (var p in props.Properties.OfType<ObjectProperty>())
                        {
                            var key = PropertyName(p);
                            // 有独立 label 时，text 可能同时充当测试或路由身份，只处理 label。
                            if (key == "text" && props.Properties.OfType<ObjectProperty>().Any(other => PropertyName(other) == "label")) continue;
                            if (TextProperties.Contains(key) || key == "children") values.Add((p.Value, key));
                        }
                    if (renderMethod.Name == "createElement")
                        for (var i = 2; i < call.Arguments.Count; i++) values.Add((call.Arguments[i], "children"));
                    var tag = call.Arguments[0] is StringLiteral name ? name.Value : "component";
                    if (tag is not ("style" or "script" or "code" or "pre" or "kbd" or "textarea") &&
                        (tag != "option" || call.Arguments[1] is ObjectExpression option && option.Properties.OfType<ObjectProperty>().Any(p => PropertyName(p) == "value")))
                        Group(call, "ui/" + tag, values);
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
            "size" or "created" or "actions" or "permissions" or "delete" or "start" or "stop" or "run" or "and" or "or";
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
            if (text.Shape == "screen-label")
            {
                ProtectedTerms.Validate(text.Source, translations[text.Key]);
                return ",label:" + Quote(translations[text.Key]);
            }
            if (text.Shape == "lookup")
            {
                var choices = texts.Where(t => t.Start == text.Start && t.End == text.End && t.Shape == "lookup" && translations.ContainsKey(t.Key))
                    .DistinctBy(t => t.Source).ToArray();
                foreach (var choice in choices) ProtectedTerms.Validate(choice.Source, translations[choice.Key]);
                // 在显示调用处转换值；生产它的配置、标识符和业务比较保持原样，表达式只求值一次。
                var body = string.Join("", choices.Select(t => $"value==={Quote(t.Source)}?{Quote(translations[t.Key])}:")) + "value";
                return $"((value)=>{body})({code[text.Start..text.End]})";
            }
            var translation = translations[text.Key];
            var expected = Enumerable.Range(0, text.Slots.Count).Select(i => $"⟪AGC{i}⟫");
            // 保持表达式原有求值顺序，避免翻译改变函数调用等副作用。
            if (!expected.SequenceEqual(Slots().Matches(translation).Select(m => m.Value)))
                throw new InvalidDataException($"动态内容的数量或顺序发生变化：{text.File}");
            ProtectedTerms.Validate(text.Source, translation);
            if (text.Shape == "string") return Quote(translation);
            var output = new StringBuilder("`"); var position = 0;
            foreach (Match slot in Slots().Matches(translation))
            {
                output.Append(EscapeTemplate(translation[position..slot.Index]));
                var expression = text.Slots[int.Parse(slot.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)];
                output.Append("${").Append(Range(expression.Start, expression.End)).Append('}');
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
