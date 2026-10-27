using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Scans HotUpdate source files for fields marked with [SerializableField] and
/// emits partial classes that implement ISerializable. The generated
/// Serialize/Deserialize methods call the matching ReadWriteStream overloads
/// for each field type. Derived classes are emitted after their base types and
/// chain base.Serialize/base.Deserialize before serializing their own fields.
/// Chain roots declare virtual members; derived classes override them. Manual
/// ISerializable bases that declare virtual Serialize/Deserialize are chained too.
/// </summary>
public static class SerializableClassGenerator
{
    private const string OutputRelativePath =
        "Assets/Scripts/HotUpdate/Serialize/Generated/SerializableGenerated.cs";

    private static readonly string[] ExcludedDirectories =
    {
        "Proto/Generated",
        "ConfigParse",
        "UI/UIExportClass",
        "Logic/Serialize/Generated",
    };

    private static readonly string[] FieldModifiers =
    {
        "public", "private", "protected", "internal", "static", "readonly", "const",
        "volatile", "unsafe", "new", "sealed", "abstract", "virtual", "override", "extern", "event",
    };

    private static readonly string[] TypeModifiers =
    {
        "public", "protected", "internal", "private", "sealed", "abstract",
    };

    private static readonly Regex SerializableFieldRegex =
        new Regex(@"\[\s*SerializableField(?:Attribute)?\b[^\]]*\]", RegexOptions.Compiled);

    private static readonly Dictionary<string, SimpleTypeInfo> SimpleTypes = BuildSimpleTypes();

    [MenuItem("Tools/Serialize/Generate Serializable Classes")]
    public static void GenerateSerializableClasses()
    {
        try
        {
            Run();
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
            EditorUtility.DisplayDialog("Serializable Class Generator", "Generation failed: " + ex.Message, "OK");
        }
    }

    private static void Run()
    {
        Run(Application.dataPath);
    }

    internal static void Run(string dataPath)
    {
        var classes = new List<ClassDef>();
        var enums = new List<EnumDef>();
        var warnings = new List<string>();
        var errors = new List<string>();

        var root = Path.Combine(dataPath, "Scripts", "HotUpdate");
        if (!Directory.Exists(root))
        {
            errors.Add("HotUpdate directory not found: " + root);
            Debug.LogError("[SerializableClassGenerator] " + errors[0]);
            return;
        }

        var outputPath = Path.Combine(dataPath, "Scripts", "HotUpdate", "Serialize", "Generated",
            "SerializableGenerated.cs");
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsSameOutputFile(f, outputPath))
            .Where(IsIncludedFile)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in files)
        {
            string source;
            try
            {
                source = File.ReadAllText(file);
            }
            catch (Exception ex)
            {
                errors.Add("Read failed: " + file + " - " + ex.Message);
                continue;
            }

            source = source.Replace("\r\n", "\n").Replace('\r', '\n');
            var relative = "Assets/" + file.Substring(dataPath.Length + 1).Replace('\\', '/');
            ParseFile(StripCommentsAndStrings(source), relative, classes, enums, warnings);
        }

        MergePartialDeclarations(classes, warnings);

        foreach (var c in classes)
            ValidateClass(c, classes, warnings);
        foreach (var c in classes)
            ValidateDescendants(classes, warnings);
        foreach (var c in classes)
            ResolveFieldCodecs(c, classes, enums, warnings);

        var generatedText = BuildGeneratedFile(classes);
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);
        File.WriteAllText(outputPath, generatedText, new UTF8Encoding(false));
        AssetDatabase.Refresh();

        var generatedCount = classes.Count(c => c.Fields.Count > 0 && c.CanGenerateOwnFields);

        Debug.Log("[SerializableClassGenerator] Generated " + generatedCount + " partial type(s) -> " +
                  OutputRelativePath);
        foreach (var w in warnings.Distinct())
            Debug.LogWarning("[SerializableClassGenerator] " + w);
        foreach (var e in errors.Distinct())
            Debug.LogError("[SerializableClassGenerator] " + e);
        if (warnings.Count > 0 || errors.Count > 0)
        {
            Debug.Log("[SerializableClassGenerator] Note: preprocessor conditionals are not evaluated; " +
                      "types referenced by [SerializableField] need a public parameterless constructor (or be a struct).");
        }
    }

    private static void ParseFile(string cleaned, string relative, List<ClassDef> classes, List<EnumDef> enums,
        List<string> warnings)
    {
        var fileScopedNs = "";
        var nsMatch = Regex.Match(cleaned, @"\bnamespace\s+([A-Za-z_][A-Za-z0-9_.]*)\s*;");
        if (nsMatch.Success)
            fileScopedNs = nsMatch.Groups[1].Value;

        var nsBlocks = new List<NamespaceBlock>();
        var fileClasses = new List<ClassDef>();
        var fileEnums = new List<EnumDef>();

        var len = cleaned.Length;
        var i = 0;
        while (i < len)
        {
            var ch = cleaned[i];
            if (!char.IsLetter(ch) && ch != '_')
            {
                i++;
                continue;
            }

            var word = ReadWord(cleaned, i);
            if (word.Length == 0)
            {
                i++;
                continue;
            }

            if (word == "namespace" && fileScopedNs.Length == 0)
            {
                var nameStart = i + word.Length;
                var name = ReadQualifiedName(cleaned, ref nameStart);
                if (name.Length > 0)
                {
                    var afterName = nameStart;
                    while (afterName < len && char.IsWhiteSpace(cleaned[afterName]))
                        afterName++;
                    if (afterName < len && cleaned[afterName] == ';')
                    {
                        fileScopedNs = name;
                        i = afterName + 1;
                        continue;
                    }

                    if (afterName < len && cleaned[afterName] == '{')
                    {
                        var close = FindMatchingBrace(cleaned, afterName);
                        if (close >= 0)
                        {
                            nsBlocks.Add(new NamespaceBlock { Name = name, Open = afterName, Close = close });
                            i = afterName + 1;
                            continue;
                        }
                    }
                }

                i += word.Length;
                continue;
            }

            if (word == "class" || word == "struct")
            {
                var keywordIndex = i;
                var after = i + word.Length;
                while (after < len && char.IsWhiteSpace(cleaned[after]))
                    after++;
                var name = ReadWord(cleaned, after);
                if (name.Length == 0)
                {
                    i = after;
                    continue;
                }

                after += name.Length;

                var typeParams = "";
                var afterTypeParams = after;
                while (afterTypeParams < len && char.IsWhiteSpace(cleaned[afterTypeParams]))
                    afterTypeParams++;
                if (afterTypeParams < len && cleaned[afterTypeParams] == '<')
                {
                    var closeAngle = FindMatchingAngle(cleaned, afterTypeParams);
                    if (closeAngle >= 0)
                    {
                        typeParams = cleaned.Substring(afterTypeParams, closeAngle - afterTypeParams + 1);
                        after = closeAngle + 1;
                    }
                }

                var bodyStart = cleaned.IndexOf('{', after);
                if (bodyStart < 0)
                {
                    i = after;
                    continue;
                }

                var bodyEnd = FindMatchingBrace(cleaned, bodyStart);
                if (bodyEnd < 0)
                {
                    i = after;
                    continue;
                }

                var declPrefix = ExtractDeclarationPrefix(cleaned, keywordIndex);
                var header = cleaned.Substring(keywordIndex, bodyStart - keywordIndex);

                fileClasses.Add(new ClassDef
                {
                    Name = name,
                    TypeParams = typeParams,
                    HeaderText = header,
                    DeclPrefix = declPrefix,
                    ModifierText = ExtractModifierText(declPrefix),
                    IsStruct = word == "struct",
                    IsPartial = Regex.IsMatch(declPrefix, @"\bpartial\b"),
                    ImplementsSerializable = Regex.IsMatch(header, @"\bISerializable\b"),
                    KeywordIndex = keywordIndex,
                    BodyStart = bodyStart,
                    BodyEnd = bodyEnd,
                    SourcePath = relative,
                });

                i = bodyStart + 1;
                continue;
            }

            if (word == "enum")
            {
                var keywordIndex = i;
                var after = i + word.Length;
                while (after < len && char.IsWhiteSpace(cleaned[after]))
                    after++;
                var name = ReadWord(cleaned, after);
                if (name.Length == 0)
                {
                    i = after;
                    continue;
                }

                after += name.Length;
                var bodyStart = cleaned.IndexOf('{', after);
                if (bodyStart < 0)
                {
                    i = after;
                    continue;
                }

                var bodyEnd = FindMatchingBrace(cleaned, bodyStart);
                if (bodyEnd < 0)
                {
                    i = after;
                    continue;
                }

                fileEnums.Add(new EnumDef { Name = name, KeywordIndex = keywordIndex, SourcePath = relative });
                i = bodyStart + 1;
                continue;
            }

            i += word.Length;
        }

        foreach (var c in fileClasses)
            c.Namespace = fileScopedNs.Length > 0 ? fileScopedNs : ResolveNamespace(c.KeywordIndex, nsBlocks);
        foreach (var e in fileEnums)
            e.Namespace = fileScopedNs.Length > 0 ? fileScopedNs : ResolveNamespace(e.KeywordIndex, nsBlocks);

        foreach (var c in fileClasses)
            c.Outer = fileClasses.FirstOrDefault(o => o != c && o.BodyStart < c.KeywordIndex && c.BodyEnd < o.BodyEnd);

        foreach (var c in fileClasses)
            c.BodyText = BuildFieldRegion(cleaned, fileClasses, c);

        foreach (var c in fileClasses)
            c.BaseTypeName = ExtractBaseTypeName(c.HeaderText);

        foreach (var c in fileClasses)
        {
            var region = BuildFieldRegion(cleaned, fileClasses, c);
            foreach (Match m in SerializableFieldRegex.Matches(region))
            {
                var line = ComputeLine(cleaned, c.BodyStart + 1 + m.Index);
                if (!TryParseField(region, m.Index + m.Length, out var fields, out var error))
                {
                    warnings.Add(relative + "(" + line + "): " + error);
                    continue;
                }

                foreach (var f in fields)
                {
                    f.SourcePath = relative;
                    f.Line = line;
                }

                c.Fields.AddRange(fields);
            }
        }

        classes.AddRange(fileClasses);
        enums.AddRange(fileEnums);
    }

    private static void ValidateClass(ClassDef c, List<ClassDef> classes, List<string> warnings)
    {
        if (c.Fields.Count == 0)
            return;

        if (c.Outer != null)
        {
            warnings.Add(c.SourcePath + ": nested type '" + c.Name +
                         "' uses [SerializableField]; nested types are not supported yet, skipped");
            c.Fields.Clear();
            return;
        }

        if (Regex.IsMatch(c.DeclPrefix, @"\brecord\b"))
        {
            warnings.Add(c.SourcePath + ": record type '" + c.Name +
                         "' is not supported yet, skipped");
            c.Fields.Clear();
            return;
        }

        if (c.IsStruct && Regex.IsMatch(c.DeclPrefix, @"\breadonly\b"))
        {
            warnings.Add(c.SourcePath + ": readonly struct '" + c.Name +
                         "' cannot be written in Deserialize, skipped");
            c.Fields.Clear();
            return;
        }

        if (!c.IsPartial)
        {
            warnings.Add(c.SourcePath + ": type '" + c.Name +
                         "' uses [SerializableField] but is not declared 'partial'; add 'partial' and regenerate");
            c.Fields.Clear();
            return;
        }

        if (c.ImplementsSerializable)
        {
            warnings.Add(c.SourcePath + ": type '" + c.Name +
                         "' already implements ISerializable; remove the attribute or the manual implementation");
            c.Fields.Clear();
            return;
        }

        c.CanGenerateOwnFields = true;
    }

    private static void ValidateDescendants(List<ClassDef> classes, List<string> warnings)
    {
        foreach (var c in classes)
        {
            if (c.Fields.Count == 0)
                continue;

            var invalidAncestor = FindNearestInvalidSerializableAncestor(c, classes);
            if (invalidAncestor == null)
                continue;

            warnings.Add(c.SourcePath + ": derived type '" + c.Name +
                         "' inherits unsupported serializable base '" + invalidAncestor.Name + "', skipped");
            c.Fields.Clear();
        }
    }

    private static ClassDef FindNearestInvalidSerializableAncestor(ClassDef c, List<ClassDef> classes)
    {
        var current = c;
        var visited = new HashSet<ClassDef> { c };
        while (TryResolveBaseType(current.BaseTypeName, current, classes, out var parent))
        {
            if (!visited.Add(parent))
                break;

            if (parent.Fields.Count > 0 && !parent.CanGenerateOwnFields)
                return parent;

            current = parent;
        }

        return null;
    }

    private static ClassDef FindNearestSerializableBase(ClassDef c, List<ClassDef> classes)
    {
        var current = c;
        var visited = new HashSet<ClassDef> { c };
        while (TryResolveBaseType(current.BaseTypeName, current, classes, out var parent))
        {
            if (!visited.Add(parent))
                break;
            
            if (DeclaresSealedSerialization(parent))
                return null;

            if (IsChainableSerializableBase(parent))
                return parent;

            current = parent;
        }

        return null;
    }

    private static int GetTypeDepth(ClassDef c, List<ClassDef> classes)
    {
        var depth = 0;
        var current = c;
        var visited = new HashSet<ClassDef> { c };
        while (TryResolveBaseType(current.BaseTypeName, current, classes, out var parent))
        {
            if (!visited.Add(parent))
                break;

            depth++;
            current = parent;
        }

        return depth;
    }

    private static void ResolveFieldCodecs(ClassDef c, List<ClassDef> classes, List<EnumDef> enums,
        List<string> warnings)
    {
        if (c.Fields.Count == 0)
            return;

        for (var i = c.Fields.Count - 1; i >= 0; i--)
        {
            var f = c.Fields[i];
            if (!TryCreateCodec(f.TypeText, c, classes, enums, out var codec, out var error))
            {
                warnings.Add(f.SourcePath + "(" + f.Line + "): field '" + c.Name + "." + f.Name +
                             "' has unsupported type '" +
                             f.TypeText + "' (" + error + "), skipped");
                c.Fields.RemoveAt(i);
                continue;
            }

            f.Codec = codec;
        }
    }

    private static string BuildGeneratedFile(List<ClassDef> classes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("// Generated by SerializableClassGenerator. Do not edit manually.");
        sb.AppendLine("// Regenerate from the Unity menu: Tools/Serialize/Generate Serializable Classes");
        sb.AppendLine("// Referenced ISerializable types need a public parameterless constructor (or be a struct).");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine();

        var toGenerate = classes
            .Where(c => c.Fields.Count > 0 && c.CanGenerateOwnFields)
            .OrderBy(c => c.Namespace, StringComparer.Ordinal)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        foreach (var group in toGenerate.GroupBy(c => c.Namespace)
                     .OrderBy(g => g.Min(c => GetTypeDepth(c, classes))))
        {
            var hasNamespace = group.Key.Length > 0;
            if (hasNamespace)
            {
                sb.AppendLine("namespace " + group.Key);
                sb.AppendLine("{");
            }

            foreach (var c in group.OrderBy(c => GetTypeDepth(c, classes)).ThenBy(c => c.Name, StringComparer.Ordinal))
                WriteGeneratedClass(sb, c, hasNamespace ? "    " : "", classes);
            if (hasNamespace)
            {
                sb.AppendLine("}");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static void WriteGeneratedClass(
        StringBuilder sb, ClassDef c, string indent, List<ClassDef> classes)
    {
        var kind = c.IsStruct ? "struct" : "class";
        var canChainBase = FindNearestSerializableBase(c, classes) != null;
        var modifierPrefix = c.ModifierText.Length > 0 ? c.ModifierText + " " : "";
        sb.AppendLine(indent + modifierPrefix + "partial " + kind + " " + c.Name + c.TypeParams +
                      " : global::ISerializable");
        sb.AppendLine(indent + "{");
        sb.AppendLine(indent + (canChainBase
            ? "    public override void Serialize(global::ReadWriteStream stream)"
            : "    public virtual void Serialize(global::ReadWriteStream stream)"));
        sb.AppendLine(indent + "    {");
        if (canChainBase)
        {
            sb.AppendLine(indent + "        base.Serialize(stream);");
            sb.AppendLine();
        }

        foreach (var f in c.Fields)
        {
            sb.AppendLine(indent + "        {");
            sb.Append(IndentAll(f.Codec.Write(new VariableNames(), "this." + f.Name), indent + "            "));
            sb.AppendLine(indent + "        }");
        }

        sb.AppendLine(indent + "    }");
        sb.AppendLine();
        sb.AppendLine(indent + (canChainBase
            ? "    public override void Deserialize(global::ReadWriteStream stream)"
            : "    public virtual void Deserialize(global::ReadWriteStream stream)"));
        sb.AppendLine(indent + "    {");
        if (canChainBase)
        {
            sb.AppendLine(indent + "        base.Deserialize(stream);");
            sb.AppendLine();
        }

        foreach (var f in c.Fields)
        {
            sb.AppendLine(indent + "        {");
            sb.Append(IndentAll(f.Codec.ReadAssign(new VariableNames(), "this." + f.Name), indent + "            "));
            sb.AppendLine(indent + "        }");
        }

        sb.AppendLine(indent + "    }");
        sb.AppendLine(indent + "}");
        sb.AppendLine();
    }

    private static bool TryParseField(string text, int start, out List<FieldDef> fields, out string error)
    {
        fields = new List<FieldDef>();
        error = null;
        var len = text.Length;
        var i = start;

        while (i < len)
        {
            while (i < len && char.IsWhiteSpace(text[i]))
                i++;
            if (i < len && text[i] == '[')
            {
                var close = text.IndexOf(']', i + 1);
                if (close < 0)
                {
                    error = "unterminated attribute group";
                    return false;
                }

                i = close + 1;
                continue;
            }

            break;
        }

        var modifiers = new List<string>();
        while (i < len)
        {
            var found = false;
            foreach (var mod in FieldModifiers)
            {
                if (i + mod.Length > len)
                    continue;
                if (string.Compare(text, i, mod, 0, mod.Length, StringComparison.Ordinal) != 0)
                    continue;
                if (i + mod.Length < len && !char.IsWhiteSpace(text[i + mod.Length]))
                    continue;
                modifiers.Add(mod);
                i += mod.Length;
                while (i < len && char.IsWhiteSpace(text[i]))
                    i++;
                found = true;
                break;
            }

            if (!found)
                break;
        }

        if (modifiers.Contains("const") || modifiers.Contains("static") ||
            modifiers.Contains("readonly") || modifiers.Contains("event"))
        {
            error = "unsupported modifier(s): " + string.Join(" ", modifiers);
            return false;
        }

        var depthParen = 0;
        var depthBracket = 0;
        var depthBrace = 0;
        var depthAngle = 0;
        var sawTopLevelEquals = false;
        var end = -1;
        var j = i;
        while (j < len)
        {
            var ch = text[j];
            if (ch == '(')
            {
                depthParen++;
            }
            else if (ch == ')')
            {
                depthParen--;
            }
            else if (ch == '[')
            {
                depthBracket++;
            }
            else if (ch == ']')
            {
                depthBracket--;
            }
            else if (ch == '{')
            {
                if (depthParen == 0 && depthBracket == 0 && depthBrace == 0 && depthAngle == 0 && !sawTopLevelEquals)
                {
                    error = "expected a field, found a property/method-like member";
                    return false;
                }

                depthBrace++;
            }
            else if (ch == '}')
            {
                if (depthParen == 0 && depthBracket == 0 && depthBrace == 0 && depthAngle == 0)
                {
                    error = "reached end of type body";
                    return false;
                }

                depthBrace--;
            }
            else if (ch == '<')
            {
                depthAngle++;
            }
            else if (ch == '>')
            {
                if (depthAngle > 0)
                    depthAngle--;
            }
            else if (ch == '=' && depthParen == 0 && depthBracket == 0 && depthBrace == 0 && depthAngle == 0)
            {
                sawTopLevelEquals = true;
            }
            else if (ch == ';' && depthParen == 0 && depthBracket == 0 && depthBrace == 0 && depthAngle == 0)
            {
                end = j;
                break;
            }

            j++;
        }

        if (end < 0)
        {
            error = "field declaration not terminated";
            return false;
        }

        var declText = text.Substring(i, end - i);
        var parts = SplitTopLevel(declText, ',');
        if (parts.Count == 0)
        {
            error = "empty field declaration";
            return false;
        }

        var firstPart = TrimInitializer(parts[0]);
        var nameMatch = Regex.Match(firstPart, @"[A-Za-z_][A-Za-z0-9_]*\s*$");
        if (!nameMatch.Success)
        {
            error = "field name not found in '" + firstPart + "'";
            return false;
        }

        var firstName = nameMatch.Value.Trim();
        var typeText = firstPart.Substring(0, nameMatch.Index).Trim();
        if (typeText.Length == 0)
        {
            error = "field type not found";
            return false;
        }

        var normalized = Regex.Replace(typeText, @"\s+", "");
        fields.Add(new FieldDef { Name = firstName, TypeText = normalized });
        for (var p = 1; p < parts.Count; p++)
        {
            var partName = TrimInitializer(parts[p]).Trim();
            if (partName.Length == 0)
                continue;
            fields.Add(new FieldDef { Name = partName, TypeText = normalized });
        }

        return true;
    }

    private static bool TryCreateCodec(string type, ClassDef owner, List<ClassDef> classes, List<EnumDef> enums,
        out Codec codec, out string error)
    {
        codec = null;
        error = null;

        if (type.StartsWith("global::"))
            type = type.Substring("global::".Length);

        if (type == "byte[]")
        {
            codec = SimpleCodec("stream.WriteBytes({0});", "stream.ReadBytes()", "byte[]");
            return true;
        }

        if (type == "int[]")
        {
            codec = SimpleCodec("stream.WriteArray({0});", "stream.ReadArrayInt()", "int[]");
            return true;
        }

        if (type == "bool[]")
        {
            codec = SimpleCodec("stream.WriteArray({0});", "stream.ReadArrayBool()", "bool[]");
            return true;
        }

        if (type == "Vector2[]" || type == "UnityEngine.Vector2[]")
        {
            codec = SimpleCodec("stream.WriteArray({0});", "stream.ReadArrayVector2()",
                "global::UnityEngine.Vector2[]");
            return true;
        }

        if (type == "Vector3[]" || type == "UnityEngine.Vector3[]")
        {
            codec = SimpleCodec("stream.WriteArray({0});", "stream.ReadArrayVector3()",
                "global::UnityEngine.Vector3[]");
            return true;
        }

        if (SimpleTypes.TryGetValue(type, out var simple))
        {
            codec = SimpleCodec(simple.WriteFormat, simple.ReadExpr, simple.TypeName);
            return true;
        }

        if (type.EndsWith("[]") && !type.Contains(","))
        {
            var elemType = type.Substring(0, type.Length - 2);
            if (!TryCreateCodec(elemType, owner, classes, enums, out var elemCodec, out error))
                return false;
            codec = BuildArrayCodec(elemCodec, elemCodec.TypeName);
            return true;
        }

        if (type.EndsWith("[,]"))
        {
            var elemType = type.Substring(0, type.Length - 3);
            if (!TryCreateCodec(elemType, owner, classes, enums, out var elemCodec, out error))
                return false;
            codec = Build2DArrayCodec(elemCodec, elemCodec.TypeName);
            return true;
        }

        if (type.StartsWith("List<") && type.EndsWith(">") && !type.Contains(","))
        {
            var elemType = type.Substring(5, type.Length - 6);
            if (!TryCreateCodec(elemType, owner, classes, enums, out var elemCodec, out error))
                return false;
            codec = BuildListCodec(elemCodec, elemCodec.TypeName);
            return true;
        }

        if (TryResolveEnum(type, owner, enums, out var enumFullName))
        {
            codec = SimpleCodec("stream.WriteInt32((int)({0}));", "(" + enumFullName + ")stream.ReadInt32()",
                enumFullName);
            return true;
        }

        if (TryResolveSerializable(type, owner, classes, out var serializableFullName))
        {
            codec = new Codec
            {
                TypeName = serializableFullName,
                Write = (vars, e) => "stream.Write(" + e + ");",
                ReadAssign = (vars, lv) =>
                {
                    var valueName = vars.New("value");
                    return "var " + valueName + " = new " + serializableFullName + "();\n" +
                           valueName + ".Deserialize(stream);\n" +
                           lv + " = " + valueName + ";";
                },
                ReadAdd = (vars, list) =>
                {
                    var itemName = vars.New("item");
                    return "var " + itemName + " = new " + serializableFullName + "();\n" +
                           itemName + ".Deserialize(stream);\n" +
                           list + ".Add(" + itemName + ");";
                },
            };
            return true;
        }

        error = "no matching ReadWriteStream method";
        return false;
    }

    private static readonly Regex OverridableMethodRegex = new Regex(
        @"(?<!\bsealed\s+)\b(?:virtual|abstract|override)\s+void\s+(?<name>Serialize|Deserialize)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex SealedSerializationRegex = new Regex(
        @"\bsealed\s+override\s+void\s+(?<name>Serialize|Deserialize)\s*\(",
        RegexOptions.Compiled);

    private static bool DeclaresSealedSerialization(ClassDef c)
    {
        if (string.IsNullOrEmpty(c.BodyText))
            return false;

        var methods = new HashSet<string>();
        foreach (Match m in SealedSerializationRegex.Matches(c.BodyText))
            methods.Add(m.Groups["name"].Value);
        return methods.Contains("Serialize") || methods.Contains("Deserialize");
    }

    private static bool IsChainableSerializableBase(ClassDef c)
    {
        if (c.Fields.Count > 0 && c.CanGenerateOwnFields)
            return true;

        return c.ImplementsSerializable && DeclaresOverridableSerialization(c);
    }

    private static bool DeclaresOverridableSerialization(ClassDef c)
    {
        if (string.IsNullOrEmpty(c.BodyText))
            return false;

        var methods = new HashSet<string>();
        foreach (Match m in OverridableMethodRegex.Matches(c.BodyText))
            methods.Add(m.Groups["name"].Value);
        return methods.Contains("Serialize") && methods.Contains("Deserialize");
    }

    private static string ExtractBaseTypeName(string header)
    {
        var keywordMatch = Regex.Match(header, @"\b(?:class|struct)\s+");
        if (!keywordMatch.Success)
            return null;

        var index = SkipIdentifier(header, keywordMatch.Index + keywordMatch.Length);
        if (index < header.Length && header[index] == '<')
        {
            var depth = 0;
            for (; index < header.Length; index++)
            {
                if (header[index] == '<')
                    depth++;
                else if (header[index] == '>' && --depth == 0)
                {
                    index++;
                    break;
                }
            }
        }

        while (index < header.Length && char.IsWhiteSpace(header[index]))
            index++;
        if (index >= header.Length || header[index] != ':')
            return null;

        index++;
        while (index < header.Length && char.IsWhiteSpace(header[index]))
            index++;

        var start = index;
        var angleDepth = 0;
        while (index < header.Length)
        {
            var ch = header[index];
            if (angleDepth == 0 && (ch == ',' || ch == '{'))
                break;
            if (angleDepth == 0 && ch == ' ')
            {
                var rest = header.Substring(index);
                if (rest.StartsWith(" where ", StringComparison.Ordinal))
                    break;
            }
            if (ch == '<')
                angleDepth++;
            else if (ch == '>')
                angleDepth--;
            index++;
        }

        var baseName = header.Substring(start, index - start).Trim();
        return baseName.Length == 0 ? null : Regex.Replace(baseName, @"\s+", "");
    }

    private static int SkipIdentifier(string text, int index)
    {
        while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_'))
            index++;
        return index;
    }

    private static bool TryResolveBaseType(
        string baseName, ClassDef derived, List<ClassDef> classes, out ClassDef baseType)
    {
        baseType = null;
        if (string.IsNullOrEmpty(baseName))
            return false;

        if (baseName.StartsWith("global::", StringComparison.Ordinal))
            baseName = baseName.Substring("global::".Length);

        var genericIndex = baseName.IndexOf('<');
        if (genericIndex >= 0)
            baseName = baseName.Substring(0, genericIndex);

        var matches = baseName.Contains(".")
            ? classes.Where(c => (c.Namespace.Length > 0 ? c.Namespace + "." + c.Name : c.Name) == baseName).ToList()
            : classes.Where(c => c.Name == baseName).ToList();
        if (matches.Count == 0)
            return false;

        var sameNamespace = matches.Where(c => c.Namespace == derived.Namespace).ToList();
        var pick = sameNamespace.Count > 0 ? sameNamespace[0] : (matches.Count == 1 ? matches[0] : null);
        if (pick == null || ReferenceEquals(pick, derived))
            return false;

        baseType = pick;
        return true;
    }

    private static bool TryResolveEnum(string type, ClassDef owner, List<EnumDef> enums, out string fullName)
    {
        fullName = null;
        if (type.StartsWith("global::"))
            type = type.Substring("global::".Length);

        List<EnumDef> matches;
        if (type.Contains("."))
            matches = enums.Where(e => (e.Namespace.Length > 0 ? e.Namespace + "." + e.Name : e.Name) == type).ToList();
        else
            matches = enums.Where(e => e.Name == type).ToList();

        if (matches.Count == 0)
            return false;

        var sameNs = matches.Where(e => e.Namespace == owner.Namespace).ToList();
        var pick = sameNs.Count > 0 ? sameNs[0] : (matches.Count == 1 ? matches[0] : null);
        if (pick == null)
            return false;

        fullName = Qualify(pick.Namespace, pick.Name);
        return true;
    }

    private static bool TryResolveSerializable(string type, ClassDef owner, List<ClassDef> classes, out string fullName)
    {
        fullName = null;
        if (type.StartsWith("global::"))
            type = type.Substring("global::".Length);

        var candidates = classes
            .Where(c => (c.Fields.Count > 0 && c.IsPartial && c.Outer == null && !c.ImplementsSerializable) ||
                        c.ImplementsSerializable)
            .ToList();

        List<ClassDef> matches;
        if (type.Contains("."))
            matches = candidates.Where(c => (c.Namespace.Length > 0 ? c.Namespace + "." + c.Name : c.Name) == type)
                .ToList();
        else
            matches = candidates.Where(c => c.Name == type).ToList();

        if (matches.Count == 0)
            return false;

        var sameNs = matches.Where(c => c.Namespace == owner.Namespace).ToList();
        var pick = sameNs.Count > 0 ? sameNs[0] : (matches.Count == 1 ? matches[0] : null);
        if (pick == null)
            return false;

        fullName = Qualify(pick.Namespace, pick.Name);
        return true;
    }

    private static Codec SimpleCodec(string writeFormat, string readExpr, string typeName)
    {
        return new Codec
        {
            TypeName = typeName,
            Write = (vars, e) => string.Format(writeFormat, e),
            ReadAssign = (vars, lv) => lv + " = " + readExpr + ";",
            ReadAdd = (vars, list) => list + ".Add(" + readExpr + ");",
        };
    }

    private static Codec BuildArrayCodec(Codec elemCodec, string elemTypeName)
    {
        return new Codec
        {
            TypeName = elemTypeName + "[]",
            Write = (vars, e) =>
            {
                var lengthName = vars.New("length");
                var indexName = vars.New("i");
                var inner = elemCodec.Write(vars, "(" + e + ")[" + indexName + "]");
                return "var " + lengthName + " = (" + e + ")?.Length ?? 0;\n" +
                       "stream.WriteInt32(" + lengthName + ");\n" +
                       "for (var " + indexName + " = 0; " + indexName + " < " + lengthName + "; " + indexName + "++)\n" +
                       "    {\n" +
                       "        " + IndentAll(inner, "        ") +
                       "    }";
            },
            ReadAssign = (vars, lv) =>
            {
                var lengthName = vars.New("length");
                var indexName = vars.New("i");
                var inner = elemCodec.ReadAssign(vars, lv + "[" + indexName + "]");
                return "var " + lengthName + " = stream.ReadInt32();\n" +
                       lv + " = null;\n" +
                       "if (" + lengthName + " > 0)\n" +
                       "{\n" +
                       "    " + lv + " = new " + elemTypeName + "[" + lengthName + "];\n" +
                       "    for (var " + indexName + " = 0; " + indexName + " < " + lengthName + "; " + indexName + "++)\n" +
                       "    {\n" +
                       "        " + IndentAll(inner, "        ") +
                       "    }\n" +
                       "}";
            },
            ReadAdd = (vars, list) =>
            {
                var lengthName = vars.New("length");
                var indexName = vars.New("i");
                var valueName = vars.New("value");
                var inner = elemCodec.ReadAssign(vars, valueName + "[" + indexName + "]");
                return "var " + lengthName + " = stream.ReadInt32();\n" +
                       elemTypeName + "[] " + valueName + " = null;\n" +
                       "if (" + lengthName + " > 0)\n" +
                       "{\n" +
                       "    " + valueName + " = new " + elemTypeName + "[" + lengthName + "];\n" +
                       "    for (var " + indexName + " = 0; " + indexName + " < " + lengthName + "; " + indexName + "++)\n" +
                       "    {\n" +
                       "        " + IndentAll(inner, "        ") +
                       "    }\n" +
                       "}\n" +
                       list + ".Add(" + valueName + ");";
            },
        };
    }

    private static Codec Build2DArrayCodec(Codec elemCodec, string elemTypeName)
    {
        return new Codec
        {
            TypeName = elemTypeName + "[]",
            Write = (vars, e) =>
            {
                var isNullName = vars.New("isNull");
                var length0Name = vars.New("l0");
                var length1Name = vars.New("l1");
                var index0Name = vars.New("i");
                var index1Name = vars.New("j");
                var inner = elemCodec.Write(vars, "(" + e + ")[" + index0Name + ", " + index1Name + "]");
                return "var " + isNullName + " = (" + e + ") == null;\n" +
                       "stream.WriteBoolean(" + isNullName + ");\n" +
                       "if(!" + isNullName + ")\n" +
                       "{\n" +
                       "    var " + length0Name + " = " + e + ".GetLength(0);\n" +
                       "    var " + length1Name + " = " + e + ".GetLength(1);\n" +
                       "    stream.WriteInt32(" + length0Name + ");\n" +
                       "    stream.WriteInt32(" + length1Name + ");\n" +
                       "    for(var " + index0Name + " = 0; " + index0Name + " < " + length0Name + "; " + index0Name + "++)\n" +
                       "    {\n" +
                       "        for(var " + index1Name + " = 0; " + index1Name + " < " + length1Name + "; " + index1Name + "++)\n" +
                       "         {\n" +
                       "         " + IndentAll(inner, "    ") +
                       "         }\n" +
                       "    }\n" +
                       "}";
            },
            ReadAssign = (vars, lv) =>
            {
                var isNullName = vars.New("isNull");
                var length0Name = vars.New("l0");
                var length1Name = vars.New("l1");
                var index0Name = vars.New("i");
                var index1Name = vars.New("j");
                var inner = elemCodec.ReadAssign(vars, lv + "[" + index0Name + "," + index1Name + "]");
                return "var " + isNullName + " = stream.ReadBoolean();\n" +
                       "if(!" + isNullName + ")\n" +
                       "{\n" +
                       "    var " + length0Name + " = stream.ReadInt32();\n" +
                       "    var " + length1Name + " = stream.ReadInt32();\n" +
                       "    " + lv + " = new " + elemTypeName + "[" + length0Name + "," + length1Name + "];\n" +
                       "    for(var " + index0Name + " = 0; " + index0Name + " < " + length0Name + "; " + index0Name + "++)\n" +
                       "    {\n" +
                       "        for(var " + index1Name + " = 0; " + index1Name + " < " + length1Name + "; " + index1Name + "++)\n" +
                       "         {\n" +
                       "         " + IndentAll(inner, "    ") +
                       "         }\n" +
                       "    }\n" +
                       "}";
            },
            ReadAdd = (vars, list) =>
            {
                var isNullName = vars.New("isNull");
                var length0Name = vars.New("l0");
                var length1Name = vars.New("l1");
                var index0Name = vars.New("i");
                var index1Name = vars.New("j");
                var valueName = vars.New("value");
                var inner = elemCodec.ReadAssign(vars, valueName + "[" + index0Name + "," + index1Name + "]");
                return "var " + isNullName + " = stream.ReadBoolean();\n" +
                       elemTypeName + "[,] " + valueName + " = null;\n" +
                       "if(!" + isNullName + ")\n" +
                       "{\n" +
                       "    var " + length0Name + " = stream.ReadInt32();\n" +
                       "    var " + length1Name + " = stream.ReadInt32();\n" +
                       "    " + valueName + " = new " + elemTypeName + "[" + length0Name + "," + length1Name + "];\n" +
                       "    for(var " + index0Name + " = 0; " + index0Name + " < " + length0Name + "; " + index0Name + "++)\n" +
                       "    {\n" +
                       "        for(var " + index1Name + " = 0; " + index1Name + " < " + length1Name + "; " + index1Name + "++)\n" +
                       "         {\n" +
                       "         " + IndentAll(inner, "    ") +
                       "         }\n" +
                       "    }\n" +
                       "}\n" +
                       list + ".Add(" + valueName + ")";
            },
        };
    }

    private static Codec BuildListCodec(Codec elemCodec, string elemTypeName)
    {
        return new Codec
        {
            TypeName = "global::System.Collections.Generic.List<" + elemTypeName + ">",
            Write = (vars, e) =>
            {
                var countName = vars.New("count");
                var indexName = vars.New("i");
                var inner = elemCodec.Write(vars, "(" + e + ")[" + indexName + "]");
                return "var " + countName + " = (" + e + ")?.Count ?? 0;\n" +
                       "stream.WriteInt32(" + countName + ");\n" +
                       "for (var " + indexName + " = 0; " + indexName + " < " + countName + "; " + indexName + "++)\n" +
                       "    {\n" +
                       "        " + IndentAll(inner, "        ") +
                       "    }";
            },
            ReadAssign = (vars, lv) =>
            {
                var countName = vars.New("count");
                var indexName = vars.New("i");
                var inner = elemCodec.ReadAdd(vars, lv);
                return "var " + countName + " = stream.ReadInt32();\n" +
                       lv + " = null;\n" +
                       "if (" + countName + " > 0)\n" +
                       "{\n" +
                       "    " + lv + " = new global::System.Collections.Generic.List<" + elemTypeName + ">(" + countName + ");\n" +
                       "    for (var " + indexName + " = 0; " + indexName + " < " + countName + "; " + indexName + "++)\n" +
                       "    {\n" +
                       "    " + IndentAll(inner, "        ") +
                       "    }\n" +
                       "}";
            },
            ReadAdd = (vars, list) =>
            {
                var countName = vars.New("count");
                var indexName = vars.New("i");
                var valueName = vars.New("value");
                var inner = elemCodec.ReadAdd(vars, valueName);
                return "var " + countName + " = stream.ReadInt32();\n" +
                       "global::System.Collections.Generic.List<" + elemTypeName + "> " + valueName + " = null;\n" +
                       "if (" + countName + " > 0)\n" +
                       "{\n" +
                       "    " + valueName + " = new global::System.Collections.Generic.List<" + elemTypeName + ">(" + countName + ");\n" +
                       "    for (var " + indexName + " = 0; " + indexName + " < " + countName + "; " + indexName + "++)\n" +
                       "    {\n" +
                       "    " + IndentAll(inner, "        ") +
                       "    }\n" +
                       "}\n" +
                       list + ".Add(" + valueName + ");";
            },
        };
    }

    private static Dictionary<string, SimpleTypeInfo> BuildSimpleTypes()
    {
        var map = new Dictionary<string, SimpleTypeInfo>
        {
            { "byte", new SimpleTypeInfo("stream.WriteByte({0});", "stream.ReadByte()", "byte") },
            { "sbyte", new SimpleTypeInfo("stream.WriteByte((byte)({0}));", "(sbyte)stream.ReadByte()", "sbyte") },
            { "bool", new SimpleTypeInfo("stream.WriteBoolean({0});", "stream.ReadBoolean()", "bool") },
            { "short", new SimpleTypeInfo("stream.WriteInt16({0});", "stream.ReadInt16()", "short") },
            {
                "ushort", new SimpleTypeInfo("stream.WriteInt16((short)({0}));", "(ushort)stream.ReadInt16()", "ushort")
            },
            { "int", new SimpleTypeInfo("stream.WriteInt32({0});", "stream.ReadInt32()", "int") },
            { "uint", new SimpleTypeInfo("stream.WriteInt32((int)({0}));", "(uint)stream.ReadInt32()", "uint") },
            { "long", new SimpleTypeInfo("stream.WriteInt64({0});", "stream.ReadInt64()", "long") },
            { "ulong", new SimpleTypeInfo("stream.WriteInt64((long)({0}));", "(ulong)stream.ReadInt64()", "ulong") },
            { "float", new SimpleTypeInfo("stream.WriteSingle({0});", "stream.ReadSingle()", "float") },
            {
                "double", new SimpleTypeInfo("stream.WriteInt64(global::System.BitConverter.DoubleToInt64Bits({0}));",
                    "global::System.BitConverter.Int64BitsToDouble(stream.ReadInt64())", "double")
            },
            { "string", new SimpleTypeInfo("stream.WriteString({0});", "stream.ReadString()", "string") },
        };

        AddUnity(map, "Vector2", "stream.WriteVector2({0});", "stream.ReadVector2()");
        AddUnity(map, "Vector3", "stream.WriteVector3({0});", "stream.ReadVector3()");
        AddUnity(map, "Vector2Int", "stream.WriteVector2Int({0});", "stream.ReadVector2Int()");
        AddUnity(map, "Vector3Int", "stream.WriteVector3Int({0});", "stream.ReadVector3Int()");
        AddUnity(map, "Matrix4x4", "stream.WriteMatrix4x4({0});", "stream.ReadMatrix4x4()");
        AddUnity(map, "RectInt", "stream.WriteRectInt({0});", "stream.ReadRectInt()");
        AddUnity(map, "Bounds",
            "stream.WriteVector3({0}.center);stream.WriteVector3({0}.size);",
            "new global::UnityEngine.Bounds(stream.ReadVector3(), stream.ReadVector3())");

        return map;
    }

    private static void AddUnity(Dictionary<string, SimpleTypeInfo> map, string name, string writeFormat,
        string readExpr)
    {
        var info = new SimpleTypeInfo(writeFormat, readExpr, "global::UnityEngine." + name);
        map[name] = info;
        map["UnityEngine." + name] = info;
    }

    private static bool IsSameOutputFile(string fullPath, string outputPath)
    {
        return string.Equals(
            Path.GetFullPath(fullPath).Replace('\\', '/'),
            Path.GetFullPath(outputPath).Replace('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
    }
    private static bool IsIncludedFile(string fullPath)
    {
        var normalized = fullPath.Replace('\\', '/');
        return ExcludedDirectories.All(d => normalized.IndexOf("/" + d + "/", StringComparison.OrdinalIgnoreCase) < 0);
    }

    private static string StripCommentsAndStrings(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        var n = text.Length;
        while (i < n)
        {
            var c = text[i];
            if (c == '/' && i + 1 < n && text[i + 1] == '/')
            {
                while (i < n && text[i] != '\n')
                {
                    sb.Append(' ');
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < n && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(text[i] == '*' && text[i + 1] == '/'))
                {
                    sb.Append(text[i] == '\n' ? '\n' : ' ');
                    i++;
                }

                if (i + 1 < n)
                    i += 2;
                else
                    i = n;
                continue;
            }

            if (c == '"')
            {
                var isVerbatim = i > 0 && text[i - 1] == '@';
                sb.Append(' ');
                i++;
                if (isVerbatim)
                {
                    while (i < n)
                    {
                        if (text[i] == '"')
                        {
                            if (i + 1 < n && text[i + 1] == '"')
                            {
                                i += 2;
                                continue;
                            }

                            i++;
                            break;
                        }

                        sb.Append(text[i] == '\n' ? '\n' : ' ');
                        i++;
                    }
                }
                else
                {
                    while (i < n)
                    {
                        if (text[i] == '\\')
                        {
                            i += 2;
                            continue;
                        }

                        if (text[i] == '"')
                        {
                            i++;
                            break;
                        }

                        sb.Append(text[i] == '\n' ? '\n' : ' ');
                        i++;
                    }
                }

                continue;
            }

            if (c == '\'')
            {
                sb.Append(' ');
                i++;
                if (i < n && text[i] == '\\')
                    i += 2;
                else
                    i++;
                if (i < n && text[i] == '\'')
                    i++;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static string ReadWord(string text, int start)
    {
        var i = start;
        while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
            i++;
        return text.Substring(start, i - start);
    }

    private static string ReadQualifiedName(string text, ref int index)
    {
        var sb = new StringBuilder();
        while (index < text.Length)
        {
            while (index < text.Length && char.IsWhiteSpace(text[index]))
                index++;
            if (index >= text.Length)
                break;
            var word = ReadWord(text, index);
            if (word.Length == 0)
                break;
            sb.Append(word);
            index += word.Length;
            while (index < text.Length && char.IsWhiteSpace(text[index]))
                index++;
            if (index < text.Length && text[index] == '.')
            {
                sb.Append('.');
                index++;
                continue;
            }

            break;
        }

        return sb.ToString();
    }

    private static int FindMatchingBrace(string text, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            if (text[i] == '{')
                depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }

        return -1;
    }

    private static int FindMatchingAngle(string text, int openIndex)
    {
        var depth = 0;
        for (var i = openIndex; i < text.Length; i++)
        {
            if (text[i] == '<')
                depth++;
            else if (text[i] == '>')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }

        return -1;
    }

    private static string ResolveNamespace(int index, List<NamespaceBlock> blocks)
    {
        NamespaceBlock best = null;
        foreach (var b in blocks)
        {
            if (b.Open < index && index < b.Close && (best == null || b.Open > best.Open))
                best = b;
        }

        return best == null ? "" : best.Name;
    }

    private static string ExtractDeclarationPrefix(string text, int keywordIndex)
    {
        var start = keywordIndex;
        while (start > 0)
        {
            var ch = text[start - 1];
            if (ch == ';' || ch == '{' || ch == '}')
                break;
            start--;
        }

        return text.Substring(start, keywordIndex - start);
    }

    private static string ExtractModifierText(string declPrefix)
    {
        var parts = new List<string>();
        foreach (var mod in TypeModifiers)
            if (Regex.IsMatch(declPrefix, @"\b" + mod + @"\b"))
                parts.Add(mod);
        return string.Join(" ", parts);
    }

    private static string MergeModifierText(string a, string b)
    {
        var parts = new List<string>();
        foreach (var mod in TypeModifiers)
            if (Regex.IsMatch(a, @"\b" + mod + @"\b") || Regex.IsMatch(b, @"\b" + mod + @"\b"))
                parts.Add(mod);
        return string.Join(" ", parts);
    }

    private static void MergePartialDeclarations(List<ClassDef> classes, List<string> warnings)
    {
        var result = new List<ClassDef>();
        foreach (var group in classes.GroupBy(GetTypeKey).ToList())
        {
            var parts = group.ToList();
            var primary = parts[0];
            if (parts.Count == 1)
            {
                result.Add(primary);
                continue;
            }

            foreach (var extra in parts.Skip(1))
            {
                if (primary.IsStruct != extra.IsStruct)
                    warnings.Add(GroupDisplayName(primary) +
                                 " has multiple declarations that disagree on class/struct kind");
                if (primary.BaseTypeName != null && extra.BaseTypeName != null &&
                    primary.BaseTypeName != extra.BaseTypeName)
                    warnings.Add(GroupDisplayName(primary) + " has multiple declarations with different base types: '" +
                                 primary.BaseTypeName + "' vs '" + extra.BaseTypeName + "'");
                if (primary.BaseTypeName == null)
                    primary.BaseTypeName = extra.BaseTypeName;
                primary.IsPartial |= extra.IsPartial;
                primary.ImplementsSerializable |= extra.ImplementsSerializable;
                primary.ModifierText = MergeModifierText(primary.ModifierText, extra.ModifierText);
                primary.Fields.AddRange(extra.Fields);
                if (extra.BodyText.Length > 0)
                    primary.BodyText = primary.BodyText.Length > 0
                        ? primary.BodyText + "\n" + extra.BodyText
                        : extra.BodyText;
            }

            result.Add(primary);
        }

        classes.Clear();
        classes.AddRange(result);
    }

    private static string GetTypeKey(ClassDef c)
    {
        var sb = new StringBuilder();
        sb.Append(c.Namespace);
        sb.Append('/');
        var outerNames = new List<string>();
        for (var outer = c.Outer; outer != null; outer = outer.Outer)
            outerNames.Add(outer.Name);
        for (var k = outerNames.Count - 1; k >= 0; k--)
        {
            sb.Append(outerNames[k]);
            sb.Append('.');
        }

        sb.Append(c.Name);
        sb.Append('`');
        sb.Append(GetGenericArity(c.TypeParams));
        return sb.ToString();
    }

    private static string GroupDisplayName(ClassDef c)
    {
        return (c.Namespace.Length > 0 ? c.Namespace + "." : "") + c.Name;
    }

    private static int GetGenericArity(string typeParams)
    {
        if (typeParams.Length < 3 || typeParams[0] != '<')
            return 0;

        var inner = typeParams.Substring(1, typeParams.Length - 2);
        return SplitTopLevel(inner, ',').Count;
    }

    private static string BuildFieldRegion(string cleaned, List<ClassDef> fileClasses, ClassDef c)
    {
        var start = c.BodyStart + 1;
        var count = c.BodyEnd - start;
        var chars = cleaned.ToCharArray();
        foreach (var n in fileClasses)
        {
            if (n == c)
                continue;
            if (n.BodyStart > c.BodyStart && n.BodyEnd < c.BodyEnd)
            {
                for (var k = n.BodyStart; k <= n.BodyEnd && k < chars.Length; k++)
                    chars[k] = chars[k] == '\n' ? '\n' : ' ';
            }
        }

        return new string(chars, start, count);
    }

    private static int ComputeLine(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
            if (text[i] == '\n')
                line++;
        return line;
    }

    private static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var depthParen = 0;
        var depthBracket = 0;
        var depthBrace = 0;
        var depthAngle = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            switch (ch)
            {
                case '(': depthParen++; break;
                case ')': depthParen--; break;
                case '[': depthBracket++; break;
                case ']': depthBracket--; break;
                case '{': depthBrace++; break;
                case '}': depthBrace--; break;
                case '<': depthAngle++; break;
                case '>':
                    if (depthAngle > 0) depthAngle--;
                    break;
                case ',':
                    if (depthParen == 0 && depthBracket == 0 && depthBrace == 0 && depthAngle == 0)
                    {
                        parts.Add(text.Substring(start, i - start));
                        start = i + 1;
                    }

                    break;
            }
        }

        parts.Add(text.Substring(start));
        return parts;
    }

    private static string TrimInitializer(string part)
    {
        var depthParen = 0;
        var depthBracket = 0;
        var depthBrace = 0;
        var depthAngle = 0;
        for (var i = 0; i < part.Length; i++)
        {
            var ch = part[i];
            switch (ch)
            {
                case '(': depthParen++; break;
                case ')': depthParen--; break;
                case '[': depthBracket++; break;
                case ']': depthBracket--; break;
                case '{': depthBrace++; break;
                case '}': depthBrace--; break;
                case '<': depthAngle++; break;
                case '>':
                    if (depthAngle > 0) depthAngle--;
                    break;
                case '=':
                    if (depthParen == 0 && depthBracket == 0 && depthBrace == 0 && depthAngle == 0)
                        return part.Substring(0, i);
                    break;
            }
        }

        return part;
    }

    private static string IndentAll(string text, string prefix)
    {
        var lines = text.Replace("\r", "").Split('\n');
        var count = lines.Length;
        if (count > 0 && lines[count - 1].Length == 0)
            count--;

        var level = 0;
        var sb = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
                continue;

            if (line.StartsWith("}", StringComparison.Ordinal))
                level = Math.Max(0, level - 1);

            sb.Append(prefix);
            sb.Append(new string(' ', level * 4));
            sb.Append(line);
            sb.Append('\n');

            var openBraces = 0;
            var closeBraces = 0;
            foreach (var ch in line)
            {
                if (ch == '{')
                    openBraces++;
                else if (ch == '}')
                    closeBraces++;
            }

            if (line.StartsWith("}", StringComparison.Ordinal))
                closeBraces--;

            level += openBraces - closeBraces;
        }

        return sb.ToString();
    }

    private static string Qualify(string ns, string name)
    {
        return ns.Length == 0 ? name : "global::" + ns + "." + name;
    }

    private sealed class NamespaceBlock
    {
        public string Name;
        public int Open;
        public int Close;
    }

    private sealed class FieldDef
    {
        public string Name;
        public string TypeText;
        public string SourcePath;
        public int Line;
        public Codec Codec;
    }

    private sealed class ClassDef
    {
        public string Name;
        public string Namespace;
        public string TypeParams;
        public string HeaderText;
        public string DeclPrefix;
        public string ModifierText;
        public bool IsStruct;
        public bool IsPartial;
        public bool ImplementsSerializable;
        public bool CanGenerateOwnFields;
        public int KeywordIndex;
        public int BodyStart;
        public int BodyEnd;
        public ClassDef Outer;
        public string BaseTypeName;
        public string BodyText;
        public List<FieldDef> Fields = new List<FieldDef>();
        public string SourcePath;
    }

    private sealed class EnumDef
    {
        public string Name;
        public string Namespace;
        public int KeywordIndex;
        public string SourcePath;
    }

    private sealed class Codec
    {
        public string TypeName;
        public Func<VariableNames, string, string> Write;
        public Func<VariableNames, string, string> ReadAssign;
        public Func<VariableNames, string, string> ReadAdd;
    }

    private sealed class VariableNames
    {
        private readonly Dictionary<string, int> counts = new Dictionary<string, int>();

        public string New(string baseName)
        {
            if (!counts.TryGetValue(baseName, out var count))
            {
                counts[baseName] = 1;
                return baseName;
            }

            count++;
            counts[baseName] = count;
            return baseName + count;
        }
    }

    private sealed class SimpleTypeInfo
    {
        public SimpleTypeInfo(string writeFormat, string readExpr, string typeName)
        {
            WriteFormat = writeFormat;
            ReadExpr = readExpr;
            TypeName = typeName;
        }

        public string WriteFormat;
        public string ReadExpr;
        public string TypeName;
    }
}