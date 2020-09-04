using Microsoft.Language.Xml;
using PimpMyAvalonia.LanguageServer;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
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
            XmlReader xr = XmlReader.Create(new StringReader(data), new XmlReaderSettings
            {
                IgnoreWhitespace = true,
                DtdProcessing = DtdProcessing.Ignore
            });
            xr = new CompatibleXmlReader(xr, compatibilityMappings ?? new Dictionary<string, string>());

            var root = XDocument.Load(xr, LoadOptions.SetLineInfo).Root;

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
                    foreach(var ignorable in kvp.Value.Split(' '))
                    {
                        ignorableNamespaces.Add(ignorable);
                    }
                }
            }

            var doc = new XamlDocument
            {
                Root = new ParserContext(root, parsed.Root, data, namespaceAliases, ignorableNamespaces).Parse(),
                NamespaceAliases = namespaceAliases
            };

            return doc;
        }


        class ParserContext
        {
            private readonly XElement _root;
            private readonly IXmlElement _newRoot;
            private readonly string _text;
            private readonly Dictionary<string, string> _ns;
            private readonly HashSet<string> _ignorable;

            public ParserContext(XElement root, IXmlElement newRoot, string text, Dictionary<string, string> namespaceAliases, HashSet<string> ignorableNamespaces)
            {
                _root = root;
                _newRoot = newRoot;
                this._text = text;
                this._ns = namespaceAliases;
                this._ignorable = ignorableNamespaces;
            }


            XamlAstXmlTypeReference GetTypeReference(XElement el) =>
                new XamlAstXmlTypeReference(el.AsLi(), el.Name.NamespaceName, el.Name.LocalName);

            XamlAstXmlTypeReference GetTypeReference(IXmlElement el)
            {
                (string ns, string name) = ParserUtils.GetNsFromName(el.Name, _ns);
                return new XamlAstXmlTypeReference(el.AsLi(_text), ns, name);
            }

            static XamlAstXmlTypeReference ParseTypeName(IXamlLineInfo info, string typeName, XElement xel)
                => ParseTypeName(info, typeName,
                    ns => string.IsNullOrWhiteSpace(ns)
                        ? xel.GetDefaultNamespace().NamespaceName
                        : xel.GetNamespaceOfPrefix(ns)?.NamespaceName ?? "");

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

            static List<XamlAstXmlTypeReference> ParseTypeArguments(string args, XElement xel, IXamlLineInfo info)
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

            static IXamlAstValueNode ParseTextValueOrMarkupExtension(string ext, XElement xel, IXamlLineInfo info)
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

            XamlAstObjectNode ParseNewInstance(XElement el, IXmlElement newEl, bool root)
            {
                XamlAstXmlTypeReference type;
                XamlAstObjectNode i;
                if (newEl != null)
                {
                    (string _, string nodeName) = ParserUtils.GetNsFromName(newEl.Name);

                    if (nodeName.Contains("."))
                        throw ParseError(newEl.AsLi(_text), "Dots aren't allowed in type names");
                    type = GetTypeReference(newEl);
                    i = new XamlAstObjectNode(newEl.AsLi(_text), type);
                }
                else
                {
                    if (el.Name.LocalName.Contains("."))
                        throw ParseError(el.AsLi(), "Dots aren't allowed in type names");
                    type = GetTypeReference(el);
                    i = new XamlAstObjectNode(el.AsLi(), type);
                }

                if (newEl != null)
                {

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
                        else if (attrNs == XamlNamespaces.Xaml2006 &&
                                 attrName == "TypeArguments")
                            type.GenericArguments = ParseTypeArguments(attribute.Value, el, attribute.AsLi(_text));
                        // Parse as a directive
                        else if (attrNs != "" && !attrName.Contains("."))
                            i.Children.Add(new XamlAstXmlDirective(newEl.AsLi(_text),
                                attrNs, attrName, new[]
                                {
                                ParseTextValueOrMarkupExtension(attribute.Value, el, attribute.AsLi(_text))
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
                                var ns = attrNs == "" ? el.GetDefaultNamespace().NamespaceName : attrNs;
                                ptype = new XamlAstXmlTypeReference(newEl.AsLi(_text), ns, parts[0]);
                            }

                            i.Children.Add(new XamlAstXamlPropertyValueNode(el.AsLi(),
                                new XamlAstNamePropertyReference(el.AsLi(), ptype, pname, type),
                                ParseTextValueOrMarkupExtension(attribute.Value, el, attribute.AsLi(_text))));
                        }
                    }
                }
                else
                {
                    foreach (var attr in el.Attributes())
                    {
                        if (attr.Name.NamespaceName == "http://www.w3.org/2000/xmlns/" ||
                            (attr.Name.NamespaceName == "" && attr.Name.LocalName == "xmlns"))
                        {
                            if (!root)
                                throw ParseError(attr.AsLi(),
                                    "xmlns declarations are only allowed on the root element to preserve memory");
                        }
                        else if (attr.Name.NamespaceName.StartsWith("http://www.w3.org"))
                        {
                            // Silently ignore all xml-parser related attributes
                        }
                        // Parse type arguments
                        else if (attr.Name.NamespaceName == XamlNamespaces.Xaml2006 &&
                                 attr.Name.LocalName == "TypeArguments")
                            type.GenericArguments = ParseTypeArguments(attr.Value, el, attr.AsLi());
                        // Parse as a directive
                        else if (attr.Name.NamespaceName != "" && !attr.Name.LocalName.Contains("."))
                            i.Children.Add(new XamlAstXmlDirective(el.AsLi(),
                                attr.Name.NamespaceName, attr.Name.LocalName, new[]
                                {
                                ParseTextValueOrMarkupExtension(attr.Value, el, attr.AsLi())
                                }
                            ));
                        // Parse as a property
                        else
                        {
                            var pname = attr.Name.LocalName;
                            var ptype = i.Type;

                            if (pname.Contains("."))
                            {
                                var parts = pname.Split(new[] { '.' }, 2);
                                pname = parts[1];
                                var ns = attr.Name.Namespace == "" ? el.GetDefaultNamespace().NamespaceName : attr.Name.NamespaceName;
                                ptype = new XamlAstXmlTypeReference(el.AsLi(), ns, parts[0]);
                            }

                            i.Children.Add(new XamlAstXamlPropertyValueNode(el.AsLi(),
                                new XamlAstNamePropertyReference(el.AsLi(), ptype, pname, type),
                                ParseTextValueOrMarkupExtension(attr.Value, el, attr.AsLi())));
                        }
                    }
                }

                foreach (var node in el.Nodes())
                {
                    if (node is XElement elementNode && elementNode.Name.LocalName.Contains("."))
                    {
                        if (elementNode.HasAttributes)
                            throw ParseError(node.AsLi(), "Attributes aren't allowed on element properties");
                        var pair = elementNode.Name.LocalName.Split(new[] { '.' }, 2);
                        i.Children.Add(new XamlAstXamlPropertyValueNode(el.AsLi(), new XamlAstNamePropertyReference
                            (
                                el.AsLi(),
                                new XamlAstXmlTypeReference(el.AsLi(), elementNode.Name.NamespaceName,
                                    pair[0]), pair[1], type
                            ),
                            ParseValueNodeChildren(elementNode)
                        ));
                    }
                    else
                    {
                        if (node is XText text)
                            i.Children.Add(new XamlAstTextNode(node.AsLi(), text.Value.Trim()));
                        else
                        {
                            var parsed = ParseValueNode(node);
                            if (parsed != null)
                                i.Children.Add(parsed);
                        }

                    }

                }

                return i;
            }

            IXamlAstValueNode ParseValueNode(XNode node)
            {
                if (node is XElement el)
                    return ParseNewInstance(el, null, false);
                if (node is XText text)
                    return new XamlAstTextNode(node.AsLi(), text.Value.Trim());
                return null;
            }

            List<IXamlAstValueNode> ParseValueNodeChildren(XElement parent)
            {
                var lst = new List<IXamlAstValueNode>();
                foreach (var n in parent.Nodes())
                {
                    if (n is XText text)
                        lst.Add(new XamlAstTextNode(n.AsLi(), text.Value.Trim()));
                    else
                    {
                        var parsed = ParseValueNode(n);
                        if (parsed != null)
                            lst.Add(parsed);
                    }
                }
                return lst;
            }

            Exception ParseError(IXamlLineInfo line, string message) =>
                new XamlParseException(message, line.Line, line.Position);

            public XamlAstObjectNode Parse() => (XamlAstObjectNode)ParseNewInstance(_root, _newRoot, true);
        }
    }

    static class Extensions
    {
        class WrappedLineInfo : IXamlLineInfo
        {
            public WrappedLineInfo(IXmlLineInfo info)
            {
                Line = info.LineNumber;
                Position = info.LinePosition;
            }

            public WrappedLineInfo(int line, int position)
            {
                Line = line;
                Position = position;
            }

            public int Line { get; set; }
            public int Position { get; set; }
        }

        public static IXamlLineInfo AsLi(this IXmlLineInfo info)
        {
            if (!info.HasLineInfo())
                throw new InvalidOperationException("XElement doesn't have line info");
            return new WrappedLineInfo(info);
        }

        public static IXamlLineInfo AsLi(this XmlNodeSyntax info, string data)
        {
            var pos = Utils.OffsetToPosition(((XmlNodeSyntax)info).SpanStart + 1, data);
            return new WrappedLineInfo(pos.Line, pos.Character);
        }
        public static IXamlLineInfo AsLi(this IXmlElement info, string data)
        {
            var pos = Utils.OffsetToPosition(((XmlNodeSyntax)info).SpanStart + 1, data);
            return new WrappedLineInfo(pos.Line, pos.Character);
        }
    }
}
