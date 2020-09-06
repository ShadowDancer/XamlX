﻿using Microsoft.Language.Xml;
using PimpMyAvalonia.LanguageServer;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using XamlX.Ast;
using XamlX.Parsers.SystemXamlMarkupExtensionParser;

namespace XamlX.Parsers
{
#if !XAMLX_INTERNAL
    public
#endif
    class XDocumentXamlParserSettings
    {
        public Dictionary<string, string> CompatibleNamespaces { get; set; }
    }

#if !XAMLX_INTERNAL
    public
#endif
    class XDocumentXamlParser
    {

        public static XamlDocument Parse(string s, Dictionary<string, string> compatibilityMappings = null)
        {
            return Parse(new StringReader(s), compatibilityMappings);
        }

        public static XamlDocument Parse(TextReader reader, Dictionary<string, string> compatibilityMappings = null)
        {
            string data = reader.ReadToEnd();
            var buffer = new StringBuffer(data);
            var parsed = Parser.Parse(buffer);

            Dictionary<string, string> namespaceAliases = new Dictionary<string, string>();
            HashSet<string> ignorableNamespaces = new HashSet<string>();
            const string ignorableNs = "http://schemas.openxmlformats.org/markup-compatibility/2006";
            foreach (var kvp in parsed.Root.Attributes)
            {
                (string ns, string name) = ParserUtils.GetNsFromName(kvp.Key);
                if (ns == "xmlns")
                {
                    namespaceAliases[name] = kvp.Value;
                }

                if (ns == "" && name == "xmlns")
                {
                    namespaceAliases[""] = kvp.Value;
                }

                if (name == "Ignorable" && namespaceAliases.TryGetValue(ns, out var transformedNs) && transformedNs == ignorableNs)
                {
                    ignorableNamespaces.Add(ns);
                    foreach (var ignorable in kvp.Value.Split(' '))
                    {
                        ignorableNamespaces.Add(ignorable);
                    }
                }
            }

            var doc = new XamlDocument
            {
                Root = new ParserContext(parsed.Root, data, namespaceAliases, ignorableNamespaces).Parse(),
                NamespaceAliases = namespaceAliases
            };

            return doc;
        }


        class ParserContext
        {
            private readonly IXmlElement _newRoot;
            private readonly string _text;
            private readonly Dictionary<string, string> _ns;
            private readonly HashSet<string> _ignorable;

            public ParserContext(IXmlElement newRoot, string text, Dictionary<string, string> namespaceAliases, HashSet<string> ignorableNamespaces)
            {
                _newRoot = newRoot;
                this._text = text;
                this._ns = namespaceAliases;
                this._ignorable = ignorableNamespaces;
            }

            XamlAstXmlTypeReference GetTypeReference(IXmlElement el)
            {
                (string ns, string name) = ParserUtils.GetNsFromName(el.Name, _ns);
                return new XamlAstXmlTypeReference(el.AsLi(_text), ns, name);
            }

            XamlAstXmlTypeReference ParseTypeName(IXamlLineInfo info, string typeName, IXmlElement xel)
                => ParseTypeName(info, typeName,
                    ns => string.IsNullOrWhiteSpace(ns)
                        ? _ns[""]
                        : _ns[ns] ?? "");

            static XamlAstXmlTypeReference ParseTypeName(IXamlLineInfo info, string typeName, Func<string, string> prefixResolver)
            {
                var pair = typeName.Trim().Split(new[] { ':' }, 2);
                string xmlns, name;
                if (pair.Length == 1)
                {
                    xmlns = prefixResolver("");
                    name = pair[0];
                }
                else
                {
                    xmlns = prefixResolver(pair[0]);
                    if (xmlns == null)
                        throw new XamlParseException($"Namespace '{pair[0]}' is not recognized", info);
                    name = pair[1];
                }
                return new XamlAstXmlTypeReference(info, xmlns, name);
            }

            List<XamlAstXmlTypeReference> ParseTypeArguments(string args, IXmlElement xel, IXamlLineInfo info)
            {
                try
                {
                    XamlAstXmlTypeReference Parse(CommaSeparatedParenthesesTreeParser.Node node)
                    {
                        var rv = ParseTypeName(info, node.Value, xel);

                        if (node.Children.Count != 0)
                            rv.GenericArguments = node.Children.Select(Parse).ToList();
                        return rv;
                    }
                    var tree = CommaSeparatedParenthesesTreeParser.Parse(args);
                    return tree.Select(Parse).ToList();
                }
                catch (CommaSeparatedParenthesesTreeParser.ParseException e)
                {
                    throw new XamlParseException(e.Message, info);
                }
            }

            IXamlAstValueNode ParseTextValueOrMarkupExtension(string ext, IXmlElement xel, IXamlLineInfo info)
            {
                if (ext.StartsWith("{") || ext.StartsWith(@"\{"))
                {
                    if (ext.StartsWith("{}"))
                        ext = ext.Substring(2);
                    else
                    {
                        try
                        {

                            return SystemXamlMarkupExtensionParser.SystemXamlMarkupExtensionParser.Parse(info, ext,
                                t => ParseTypeName(info, t, xel));
                        }
                        catch (MeScannerParseException parseEx)
                        {
                            throw new XamlParseException(parseEx.Message, info);
                        }
                    }
                }

                return new XamlAstTextNode(info, ext);
            }

            XamlAstObjectNode ParseNewInstance(IXmlElement newEl, bool root)
            {
                XamlAstXmlTypeReference type;
                XamlAstObjectNode i;

                (string _, string elementName) = ParserUtils.GetNsFromName(newEl.Name);

                if (elementName.Contains("."))
                    throw ParseError(newEl.AsLi(_text), "Dots aren't allowed in type names");
                type = GetTypeReference(newEl);
                i = new XamlAstObjectNode(newEl.AsLi(_text), type);

                foreach (XmlAttributeSyntax attribute in newEl.AsSyntaxElement.Attributes)
                {

                    (string attrNs, string attrName) = ParserUtils.GetNsFromName(attribute.Name);
                    if (_ignorable.Contains(attrNs))
                    {
                        continue;
                    }
                    if (attrNs == "http://www.w3.org/2000/xmlns/" || attrNs == "xmlns" ||
                        (attrNs == "" && attrName == "xmlns"))
                    {

                        if (!root)
                            throw ParseError(attribute.AsLi(_text),
                                "xmlns declarations are only allowed on the root element to preserve memory");
                    }
                    else if (attrNs.StartsWith("http://www.w3.org"))
                    {
                        // Silently ignore all xml-parser related attributes
                    }
                    // Parse type arguments
                    else if (_ns[attrNs] == XamlNamespaces.Xaml2006 &&
                                attrName == "TypeArguments")
                        type.GenericArguments = ParseTypeArguments(attribute.Value, newEl, attribute.AsLi(_text));
                    // Parse as a directive
                    else if (attrNs != "" && !attrName.Contains("."))
                        i.Children.Add(new XamlAstXmlDirective(newEl.AsLi(_text),
                            _ns[attrNs], attrName, new[]
                            {
                            ParseTextValueOrMarkupExtension(attribute.Value, newEl, attribute.AsLi(_text))
                            }
                        ));
                    // Parse as a property
                    else
                    {
                        var pname = attrName;
                        var ptype = i.Type;

                        if (pname.Contains("."))
                        {
                            var parts = pname.Split(new[] { '.' }, 2);
                            pname = parts[1];
                            var ns = attrNs == "" ? _ns[""] : _ns[attrNs];
                            ptype = new XamlAstXmlTypeReference(newEl.AsLi(_text), ns, parts[0]);
                        }

                        i.Children.Add(new XamlAstXamlPropertyValueNode(newEl.AsLi(_text),
                            new XamlAstNamePropertyReference(newEl.AsLi(_text), ptype, pname, type),
                            ParseTextValueOrMarkupExtension(attribute.Value, newEl, attribute.AsLi(_text))));
                    }
                }

                foreach (var newNode in newEl.Elements)
                {
                    (string nodeNs, string nodeName) = ParserUtils.GetNsFromName(newNode.Name);
                    if (_ignorable.Contains(nodeNs))
                    {
                        continue;
                    }

                   if (nodeName.Contains("."))
                    {
                        if (newNode.Attributes.Any())
                            throw ParseError(newNode.AsLi(_text), "Attributes aren't allowed on element properties");
                        var pair = nodeName.Split(new[] { '.' }, 2);
                        i.Children.Add(new XamlAstXamlPropertyValueNode(newEl.AsLi(_text), new XamlAstNamePropertyReference
                            (
                                newEl.AsLi(_text),
                                new XamlAstXmlTypeReference(newEl.AsLi(_text), _ns[nodeNs],
                                    pair[0]), pair[1], type
                            ),
                            ParseValueNodeChildren(newNode)
                        ));
                    }
                    else
                    {
                        var parsed = ParseValueNode(newNode);
                        if (parsed != null)
                            i.Children.Add(parsed);
                    }

                }

                SyntaxList<SyntaxNode> syntaxContent = newEl.AsSyntaxElement.Content;
                if (syntaxContent.Count == 1 && syntaxContent[0] is XmlTextSyntax textContent)
                {
                    i.Children.Add(new XamlAstTextNode(textContent.AsLi(_text), textContent.Value.Trim()));
                }

                return i;
            }

            IXamlAstValueNode ParseValueNode(IXmlElement newNode)
            {
                if(newNode.AsSyntaxElement.Content.Count == 1 && newNode.AsSyntaxElement.Content[0] is XmlTextSyntax textContent)
                {
                    return new XamlAstTextNode(newNode.AsLi(_text), textContent.Value.Trim());
                }
                else
                {
                    return ParseNewInstance(newNode, false);
                }
            }

            List<IXamlAstValueNode> ParseValueNodeChildren(IXmlElement newParent)
            {
                var lst = new List<IXamlAstValueNode>();

                if (newParent != null && newParent.AsSyntaxElement.Content.Count == 1 && newParent.AsSyntaxElement.Content[0] is XmlTextSyntax textContent)
                {
                    lst.Add(new XamlAstTextNode(textContent.AsLi(_text), textContent.Value.Trim()));
                }
                else
                {
                    foreach (var newNode in newParent.Elements)
                    {
                        var parsed = ParseValueNode(newNode);
                        if (parsed != null)
                            lst.Add(parsed);
                    }
                }
                return lst;
            }

            Exception ParseError(IXamlLineInfo line, string message) =>
                new XamlParseException(message, line.Line, line.Position);

            public XamlAstObjectNode Parse() => (XamlAstObjectNode)ParseNewInstance(_newRoot, true);
        }
    }

    static class Extensions
    {
        class WrappedLineInfo : IXamlLineInfo
        {
            public WrappedLineInfo(int line, int position)
            {
                Line = line;
                Position = position;
            }

            public int Line { get; set; }
            public int Position { get; set; }
        }

        public static IXamlLineInfo AsLi(this SyntaxNode info, string data)
        {
            var pos = Utils.OffsetToPosition(((SyntaxNode)info).SpanStart + 1, data);
            return new WrappedLineInfo(pos.Line, pos.Character);
        }
        public static IXamlLineInfo AsLi(this IXmlElement info, string data)
        {
            var pos = Utils.OffsetToPosition(((XmlNodeSyntax)info).SpanStart + 1, data);
            return new WrappedLineInfo(pos.Line, pos.Character);
        }
    }
}
