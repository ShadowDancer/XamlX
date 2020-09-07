using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using XamlX.Parsers;
using Xunit;

namespace XamlParserTests.Impl
{
    public class ParserTests
    {
        [Fact]
        public void Test()
        {

            string avaloniaDir = "C:\\Users\\przem\\source\\repos";
            var xaml = Directory.GetFiles(avaloniaDir, "*.xaml", SearchOption.AllDirectories);
            var axaml = Directory.GetFiles(avaloniaDir, "*.xaml", SearchOption.AllDirectories);

            var files = xaml.Concat(axaml).ToList();
            files.Sort();
            int total = files.Count;
            for (int i = 0; i < files.Count; i++)
            {
                string file = files[i];

                string text = File.ReadAllText(file);

                // Deal with StructDiff throwing on 0 line or character
                text = "\r\n" + text.Replace("\r\n", " \r\n");

                XamlParser.Experimental = true;
                var experimental = XamlParser.Parse(text);
                XamlParser.Experimental = false;
                var legacy = XamlParser.Parse(text);

                Helpers.StructDiff(experimental, legacy);
                i++;
            }





        }

    }
}
