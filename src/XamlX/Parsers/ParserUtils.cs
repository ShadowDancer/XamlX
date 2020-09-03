using System;
using System.Collections.Generic;
using System.Text;

namespace XamlX.Parsers
{
    public class ParserUtils
    {
        public static (string ns, string name) GetNsFromName(string name)
        {
            var colonIndex = name.LastIndexOf(":");
            if(colonIndex == -1)
            {
                return ("", name);
            }
            else
            {
                return (name.Substring(0, colonIndex), name.Substring(colonIndex+1));
            }
        }
    }
}
