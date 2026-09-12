namespace PomoDock.Core;

/// <summary>
/// GBNF that forces one JSON turn: a catalog action plus arguments. The model cannot
/// emit markdown, tool XML, or an invented tool name.
/// </summary>
public static class AgentGrammar
{
    public static string JsonTurn
    {
        get
        {
            string actions = string.Join(" | ", AgentCatalog.ModelActions.Select(name => "\"\\\"" + name + "\\\"\""));
            return """
                root ::= "{" ws "\"action\"" ws ":" ws action ws "," ws "\"arguments\"" ws ":" ws value ("," ws "\"message\"" ws ":" ws string)? ("," ws "\"understood\"" ws ":" ws string)? ws "}"
                action ::= 
                """.TrimEnd() + " " + actions + """

                value ::= object | array | string | number | boolean | "null"
                object ::= "{" ws (string ws ":" ws value (ws "," ws string ws ":" ws value)*)? ws "}"
                array ::= "[" ws (value (ws "," ws value)*)? ws "]"
                string ::= "\"" chars "\""
                chars ::= char*
                char ::= [^"\\\x00-\x1F] | "\\" (["\\/bfnrt] | "u" [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F] [0-9a-fA-F])
                number ::= "-"? ("0" | [1-9] [0-9]*) ("." [0-9]+)? ([eE] [+\-]? [0-9]+)?
                boolean ::= "true" | "false"
                ws ::= ([ \t\n\r])*
                """;
        }
    }
}
