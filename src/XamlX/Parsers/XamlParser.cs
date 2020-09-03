using System.Collections.Generic;
using System.IO;
using XamlX.Ast;

namespace XamlX.Parsers
{
    public static class XamlParser
    {
        public static XamlDocument Parse(string s, Dictionary<string, string> compatibilityMappings = null)
        {
            return GuiLabsXamlParser.Parse(s, compatibilityMappings);
        }
        public static XamlDocument Parse(TextReader reader, Dictionary<string, string> compatibilityMappings = null)
        {
            return GuiLabsXamlParser.Parse(reader, compatibilityMappings);
        }
    }
}
