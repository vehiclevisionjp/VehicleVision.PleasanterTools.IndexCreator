using System.Text.Json;
using Acornima.Ast;

namespace VehicleVision.PleasanterTools.IndexCreator;

public sealed record ScriptQuery(long TargetSiteId, JsonElement View);

/// <summary>副作用のない先頭の宣言列だけを追跡する限定的な解析。</summary>
public static class ScriptQueryResolver
{
    public static IReadOnlyList<ScriptQuery> Resolve(Node root)
    {
        var result = new List<ScriptQuery>();
        var variables = new Dictionary<string, object?>();
        // ホイスティングや同名 API の定義を見つけた場合、実 API として確定しない。
        var pending = new Stack<Node>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node is ClassDeclaration || node is VariableDeclarator { Id: not Identifier }
                || node is VariableDeclarator { Id: Identifier id } && id.Name is "items" or "JSON"
                || node is FunctionDeclaration { Id: Identifier function } && function.Name is "items" or "JSON")
            {
                return result;
            }
            foreach (var child in node.ChildNodes)
            {
                pending.Push(child);
            }
        }
        var statements = root.ChildNodes.ToArray();
        // スクリプト全体を囲う try の先頭は必ず実行される。catch/finally の値は流入させない。
        if (statements.Length == 1 && statements[0] is TryStatement wrapper)
        {
            statements = wrapper.Block.ChildNodes.ToArray();
        }
        foreach (var statement in statements)
        {
            if (statement is not VariableDeclaration declaration)
            {
                break;
            }
            foreach (var item in declaration.Declarations)
            {
                if (item.Id is not Identifier name)
                {
                    return result;
                }
                if (item.Init is CallExpression call && IsCall(call, "items", "Get") && call.Arguments.Count == 2
                    && Value(call.Arguments[0], variables, out var target) && target is double number
                    && number > 0 && number <= 9007199254740991d && number == Math.Truncate(number)
                    && call.Arguments[1] is CallExpression stringify && IsCall(stringify, "JSON", "stringify")
                    && stringify.Arguments.Count == 1 && Value(stringify.Arguments[0], variables, out var data)
                    && data is Dictionary<string, object?> query && query.TryGetValue("View", out var view)
                    && view is Dictionary<string, object?>)
                {
                    result.Add(new((long)number, JsonSerializer.SerializeToElement(view)));
                    // Get 内の副作用や後続の再代入を追跡しない。
                    return result;
                }
                if (!Value(item.Init, variables, out var value))
                {
                    return result;
                }
                // オブジェクトの別名を作らない。参照を共有すると後続の変更を追跡できない。
                if (item.Init is Identifier && value is not (null or string or double or bool))
                {
                    return result;
                }
                variables[name.Name] = value;
            }
        }
        return result;
    }

    private static bool IsCall(CallExpression call, string owner, string method) =>
        call.Callee is MemberExpression { Computed: false, Object: Identifier obj, Property: Identifier member }
        && obj.Name == owner && member.Name == method;

    private static bool Value(Node? node, Dictionary<string, object?> variables, out object? value)
    {
        value = null;
        switch (node)
        {
            case StringLiteral text: value = text.Value; return true;
            case NumericLiteral number: value = number.Value; return true;
            case BooleanLiteral boolean: value = boolean.Value; return true;
            case NullLiteral: return true;
            case Identifier id: return variables.TryGetValue(id.Name, out value);
            case ObjectExpression obj:
                var properties = new Dictionary<string, object?>();
                foreach (var entry in obj.Properties)
                {
                    if (entry is not ObjectProperty property || property.Computed || property.Method
                        || property.Value is not (StringLiteral or NumericLiteral or BooleanLiteral or NullLiteral or ObjectExpression or ArrayExpression)
                        || !Value(property.Value, variables, out var child))
                    {
                        return false;
                    }
                    var key = property.Key switch { Identifier name => name.Name, StringLiteral name => name.Value, _ => "" };
                    if (key == "" || key == "__proto__" || !properties.TryAdd(key, child))
                    {
                        return false;
                    }
                }
                value = properties; return true;
            case ArrayExpression array:
                var elements = new List<object?>();
                foreach (var element in array.Elements)
                {
                    if (element == null || element is Identifier || !Value(element, variables, out var child))
                    {
                        return false;
                    }
                    elements.Add(child);
                }
                value = elements; return true;
            default: return false;
        }
    }
}
