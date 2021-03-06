﻿using System;
using System.CodeDom;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using EnvDTE;

namespace MadsKristensen.ExtensibilityTools.VSCT.Generator
{
    /// <summary>
    /// This is the dedicated code generator class that processes VSCT files and outputs their CommandIDs/GUIDs to be used in code.
    /// When setting the 'Custom Tool' property of a C#, VB, or J# project item to "VsctGenerator", 
    /// the GenerateCode function will get called and will return the contents of the generated file to the project system.
    /// </summary>
    [Guid("a6a34300-fa6b-4f86-a8ba-e1fea8d24922")]
    public sealed class VsctCodeGenerator : BaseCodeGenerator
    {
        #region Public Names

        /// <summary>
        /// Name of this generator.
        /// </summary>
        public const string GeneratorName = "VsctGenerator";

        /// <summary>
        /// Description of this generator.
        /// </summary>
        public const string GeneratorDescription = "Generates .NET source code for given VS IDE GUI definitions.";

        #endregion

        #region Comments

        private const string DefaultGuidListClassName = "PackageGuids";
        private const string DefaultPkgCmdIDListClassName = "PackageIds";
        private const string ClassGuideListComment = "Helper class that exposes all GUIDs used across VS Package.";
        private const string ClassPkgCmdIDListComments = "Helper class that encapsulates all CommandIDs uses across VS Package.";

        #endregion

        protected override string GenerateStringCode(string inputFileContent)
        {
            string globalNamespaceName;
            string guidListClassName;
            string cmdIdListClassName;
            string supporterPostfix;
            bool isPublic;

            // get parameters passed as 'FileNamespace' inside properties of the file generator:
            InterpreteArguments((string.IsNullOrEmpty(FileNamespace) ? null : FileNamespace.Split(';')),
                                out globalNamespaceName, out guidListClassName, out cmdIdListClassName,
                                out supporterPostfix, out isPublic);

            // create support CodeDOM classes:
            var globalNamespace = new System.CodeDom.CodeNamespace(globalNamespaceName);
            var classGuideList = CreateClass(guidListClassName, ClassGuideListComment, isPublic, true);
            var classPkgCmdIDList = CreateClass(cmdIdListClassName, ClassPkgCmdIDListComments, isPublic, true);
            IList<KeyValuePair<string, string>> guids;
            IList<KeyValuePair<string, string>> ids;

            // retrieve the list GUIDs and IDs defined inside VSCT file:
            Parse(inputFileContent, out guids, out ids);

            // generate members describing GUIDs:
            if (guids != null)
            {
                var delayedMembers = new List<CodeTypeMember>();
                foreach (var symbol in guids)
                {
                    string nameString;
                    string nameGuid;

                    // for each GUID generate one string and one GUID field with similar names:
                    if (symbol.Key != null && symbol.Key.EndsWith(supporterPostfix, StringComparison.OrdinalIgnoreCase))
                    {
                        nameString = symbol.Key;
                        nameGuid = symbol.Key.Substring(0, symbol.Key.Length - supporterPostfix.Length);;
                    }
                    else
                    {
                        nameString = symbol.Key + supporterPostfix;
                        nameGuid = symbol.Key;
                    }

                    classGuideList.Members.Add(CreateConstField("System.String", nameString, symbol.Value, true));
                    delayedMembers.Add(CreateStaticField("Guid", nameGuid, nameString, true));
                }

                foreach (var member in delayedMembers)
                {
                    classGuideList.Members.Add(member);
                }
            }

            // generate members describing IDs:
            if (ids != null)
            {
                foreach (var i in ids)
                {
                    classPkgCmdIDList.Members.Add(CreateConstField("System.Int32", i.Key, ToHex(i.Value, GetProject().CodeModel.Language), false));
                }
            }

            // add all members to final namespace:
            globalNamespace.Imports.Add(new CodeNamespaceImport("System"));
            globalNamespace.Types.Add(classGuideList);
            globalNamespace.Types.Add(classPkgCmdIDList);

            // generate source code:
            return GenerateFromNamespace(GetCodeProvider(), globalNamespace, false);
        }

        private void InterpreteArguments(string[] args, out string globalNamespaceName, out string guidClassName, out string cmdIdListClassName, out string supporterPostfix, out bool isPublic)
        {
            globalNamespaceName = GetProject().Properties.Item("DefaultNamespace").Value as string;
            guidClassName = DefaultGuidListClassName;
            cmdIdListClassName = DefaultPkgCmdIDListClassName;
            supporterPostfix = "String";
            isPublic = false;

            if (args != null && args.Length != 0)
            {
                if (!string.IsNullOrEmpty(args[0]))
                    globalNamespaceName = args[0];

                if (!(args.Length < 2 || string.IsNullOrEmpty(args[1])))
                    guidClassName = args[1];

                if (!(args.Length < 3 || string.IsNullOrEmpty(args[2])))
                    cmdIdListClassName = args[2];

                if (!(args.Length < 4 || string.IsNullOrEmpty(args[3])))
                    supporterPostfix = args[3];

                if (args.Length >= 5 && !string.IsNullOrEmpty(args[4]) && string.Compare(args[4], "public", StringComparison.OrdinalIgnoreCase) == 0)
                    isPublic = true;
            }
        }

        #region Parsing

        /// <summary>
        /// Extract GUIDs and IDs descriptions from given XML content.
        /// </summary>
        private static void Parse(string vsctContentFile, out IList<KeyValuePair<string, string>> guids, out IList<KeyValuePair<string, string>> ids)
        {
            var xml = new XmlDocument();
            XmlElement symbols = null;

            guids = null;
            ids = null;

            try
            {
                xml.LoadXml(vsctContentFile);

                // having XML loaded go through and find:
                // CommandTable / Symbols / GuidSymbol* / IDSymbol*
                if (xml.DocumentElement != null && xml.DocumentElement.Name == "CommandTable")
                    symbols = xml.DocumentElement["Symbols"];
            }
            catch
            {
                return;
            }

            if (symbols != null)
            {
                var guidSymbols = symbols.GetElementsByTagName("GuidSymbol");

                guids = new List<KeyValuePair<string, string>>();
                ids = new List<KeyValuePair<string, string>>();

                foreach (XmlElement symbol in guidSymbols)
                {
                    try
                    {
                        // go through all GuidSymbol elements...
                        var value = symbol.Attributes["value"].Value;
                        var name = symbol.Attributes["name"].Value;

                        // preprocess value to remove the brackets:
                        try
                        {
                            value = new Guid(value).ToString("D");
                        }
                        catch
                        {
                            value = "-invalid-";
                        }

                        guids.Add(new KeyValuePair<string, string>(name, value));
                    }
                    catch
                    {
                    }

                    var idSymbols = symbol.GetElementsByTagName("IDSymbol");
                    foreach (XmlElement i in idSymbols)
                    {
                        try
                        {
                            // go through all IDSymbol elements...
                            ids.Add(new KeyValuePair<string, string>(i.Attributes["name"].Value, i.Attributes["value"].Value));
                        }
                        catch
                        {
                        }
                    }
                }
            }
        }

        #endregion

        #region Code Definition

        /// <summary>
        /// Creates new static/partial class definition.
        /// </summary>
        private static CodeTypeDeclaration CreateClass(string name, string comment, bool isPublic, bool isPartial)
        {
            var item = new CodeTypeDeclaration(name);

            item.Comments.Add(new CodeCommentStatement("<summary>", true));
            item.Comments.Add(new CodeCommentStatement(comment, true));
            item.Comments.Add(new CodeCommentStatement("</summary>", true));
            item.IsClass = true;
            item.IsPartial = true;

            // HINT: Sealed + Abstract => static class definition
            if (isPublic)
                item.TypeAttributes = TypeAttributes.Sealed | TypeAttributes.Public;
            else
                item.TypeAttributes = TypeAttributes.Sealed | TypeAttributes.NestedFamANDAssem;

            item.IsPartial = isPartial;

            item.TypeAttributes |= TypeAttributes.BeforeFieldInit | TypeAttributes.Class;
            return item;
        }

        /// <summary>
        /// Creates new constant field with given name and value.
        /// </summary>
        private static CodeMemberField CreateConstField(string type, string name, string value, bool fieldRef)
        {
            var item = new CodeMemberField(new CodeTypeReference(type), name);

            item.Attributes = MemberAttributes.Const | MemberAttributes.Public;
            if (fieldRef)
            {
                item.InitExpression = new CodePrimitiveExpression(value);
            }
            else
            {
                item.InitExpression = new CodeSnippetExpression(value);
            }

            return item;
        }

        /// <summary>
        /// Creates new static/read-only field with given name and value.
        /// </summary>
        private static CodeMemberField CreateStaticField(string type, string name, object value, bool fieldRef)
        {
            var item = new CodeMemberField(new CodeTypeReference(type), name);
            var param = fieldRef
                            ? (CodeExpression)new CodeSnippetExpression((string)value)
                            : new CodePrimitiveExpression(value);
            item.Attributes = MemberAttributes.Static | MemberAttributes.Public;
            item.InitExpression = new CodeObjectCreateExpression(new CodeTypeReference(type), param);

            return item;
        }

        #endregion

        #region Code Generation

        /// <summary>
        /// Generates source code from given namespace.
        /// </summary>
        private static string GenerateFromNamespace(CodeDomProvider codeProvider, System.CodeDom.CodeNamespace codeNamespace, bool blankLinesBetweenMembers)
        {
            var result = new StringBuilder();
            var writer = new StringWriter(result);

            var options = new CodeGeneratorOptions();
            options.BlankLinesBetweenMembers = blankLinesBetweenMembers;
            options.ElseOnClosing = true;
            options.VerbatimOrder = true;
            options.BracingStyle = "C";

            // generate the code:
            codeProvider.GenerateCodeFromNamespace(codeNamespace, writer, options);

            // send it to the StringBuilder object:
            writer.Flush();

            return result.ToString();
        }

        /// <summary>
        /// Converts given number into hex string.
        /// </summary>
        private static string ToHex(string number, string language)
        {
            if (!string.IsNullOrEmpty(number))
            {
                uint value;

                if (uint.TryParse(number, out value))
                    return ToHex(value, language);

                if (uint.TryParse(number, NumberStyles.HexNumber, CultureInfo.CurrentCulture, out value))
                    return ToHex(value, language);

                if ((number.StartsWith("0x") || number.StartsWith("0X") || number.StartsWith("&H")) &&
                    uint.TryParse(number.Substring(2), NumberStyles.HexNumber, CultureInfo.CurrentCulture, out value))
                    return ToHex(value, language);
            }

            // parsing failed, return string:
            return number;
        }

        /// <summary>
        /// Serialize given number into hex representation.
        /// </summary>
        private static string ToHex(uint value, string language)
        {
            switch (language)
            {
                case CodeModelLanguageConstants.vsCMLanguageVB:
                    return "&H" + value.ToString("X4");

                default:
                    return "0x" + value.ToString("X4");
            }
        }

        #endregion
    }
}
