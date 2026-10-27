#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

// Draft copy of MapPresentationBakerEditor for Beyond Compare review:
// 1. Class renamed and [CustomEditor] removed so it can coexist with the original file.
// 2. Adds scene-view editing for MapPresentationRoute.waypoints (drag/move, insert, add, delete, snap).
// 3. Adds a menu entry Tools/Map/Edit Presentation Routes (Draft Inspector).
// When promoting this draft, rename the class back to MapPresentationBakerEditor,
// restore the [CustomEditor] attribute, and delete the original file.
[CustomEditor(typeof(MapPresentationBaker))]
public sealed class MapPresentationBakerEditor : Editor
{
    private const string EditorUndoPrefix = "[MapPresentationBakerEditor]";
    private static readonly Color SelectedRowColor = new Color(0.24f, 0.47f, 0.95f, 0.18f);

    private static Type[] behaviorTypes = Array.Empty<Type>();

    private string ruleSearch = string.Empty;
    private readonly Dictionary<string, string> behaviorSearchKeywords = new Dictionary<string, string>();
    private readonly Dictionary<string, EditableListState> listStates = new Dictionary<string, EditableListState>();
    private readonly Dictionary<string, bool> listFoldouts = new Dictionary<string, bool>();

    public override void OnInspectorGUI()
    {
        EditorGUI.BeginChangeCheck();
        serializedObject.Update();
        AutoFillMapIdFromData();

        DrawBoundField("mapId", "Map Id");
        DrawBoundField("data", "Existing Bytes");
        DrawRulesPanel();
        DrawOperationButtons();

        serializedObject.ApplyModifiedProperties();
        if (EditorGUI.EndChangeCheck())
            EditorUtility.SetDirty(target);
    }

    private void OnEnable()
    {
        AutoFillMapIdFromData();
    }

    private void AutoFillMapIdFromData()
    {
        if (target is not MapPresentationBaker baker || baker.data == null) return;

        var mapId = ExtractMapIdFromName(baker.data.name);
        if (mapId == null) return;

        var property = serializedObject.FindProperty("mapId");
        if (property == null) return;

        property.intValue = mapId.Value;
        serializedObject.ApplyModifiedPropertiesWithoutUndo();
    }

    private static int? ExtractMapIdFromName(string assetName)
    {
        if (string.IsNullOrEmpty(assetName)) return null;

        var separatorIndex = assetName.LastIndexOf('@');
        if (separatorIndex < 0 || separatorIndex == assetName.Length - 1) return null;

        return int.TryParse(assetName.Substring(separatorIndex + 1), out var value)
            ? value
            : (int?)null;
    }

    private void DrawBoundField(string fieldName, string label)
    {
        var property = serializedObject.FindProperty(fieldName);
        if (property == null) return;

        EditorGUILayout.PropertyField(property, new GUIContent(label));
    }

    private void DrawOperationButtons()
    {
        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField("Operations", EditorStyles.boldLabel);

        if (GUILayout.Button("Load From Bytes"))
        {
            if (target is MapPresentationBaker baker)
            {
                Undo.RecordObject(baker, EditorUndoPrefix + " Load");
                baker.Load();
                serializedObject.Update();
            }
        }

        if (GUILayout.Button("Generate And Save"))
        {
            if (target is MapPresentationBaker baker &&
                TryConfirmOverwriteBytes(string.Format(MapDefine.PresentationDataFormat, baker.mapId)))
            {
                Undo.RecordObject(baker, EditorUndoPrefix + " Save");
                baker.Save();
                serializedObject.Update();
            }
        }

        EditorGUILayout.Space(6f);
    }

    private static bool TryConfirmOverwriteBytes(string outputPath)
    {
        if (!File.Exists(outputPath)) return true;

        return EditorUtility.DisplayDialog(
            "Map Bytes Exists",
            $"目标文件已存在：\n{outputPath}\n\n是否覆盖写入？",
            "确认",
            "取消");
    }

    private void DrawRulesPanel()
    {
        var rulesProp = serializedObject.FindProperty("rules");
        if (rulesProp == null || !rulesProp.isArray)
        {
            EditorGUILayout.HelpBox("rules: no serialized array property", MessageType.Warning);
            return;
        }

        EditorGUILayout.LabelField("Rules", EditorStyles.boldLabel);
        ruleSearch = EditorGUILayout.TextField("Filter", ruleSearch);

        var rulesState = GetOrCreateListState("rules");
        RefreshRuleIndices(rulesState, rulesProp, ruleSearch);
        var visibleIndices = rulesState.VisibleIndices;
        if (rulesProp.arraySize == 0)
        {
            if (GUILayout.Button("+ 添加规则", EditorStyles.miniButton))
            {
                rulesProp.InsertArrayElementAtIndex(rulesProp.arraySize);
                NormalizeRuleElement(rulesProp.GetArrayElementAtIndex(rulesProp.arraySize - 1), rulesProp);
                rulesState.SelectedRow = visibleIndices.Count;
                Repaint();
            }

            return;
        }

        if (!ToggleListExpandedLayout(rulesProp, "Rules")) return;

        var canDrag = string.IsNullOrWhiteSpace(ruleSearch);
        var previousOrder = new List<int>(visibleIndices);
        var lineHeight = EditorGUIUtility.singleLineHeight;
        var spacing = EditorGUIUtility.standardVerticalSpacing;

        var list = new ReorderableList(
            visibleIndices,
            typeof(int),
            canDrag,
            false,
            true,
            true);

        list.drawElementCallback = (rect, rowIndex, isActive, _) =>
        {
            if (rowIndex < 0 || rowIndex >= visibleIndices.Count) return;

            if (isActive)
                rulesState.SelectedRow = rowIndex;

            TrackRowSelection(rulesState, rect, rowIndex);
            DrawSelectionBackground(rulesState, rect, rowIndex);

            var actualIndex = visibleIndices[rowIndex];
            DrawRuleRow(rect, rulesProp.GetArrayElementAtIndex(actualIndex), rowIndex);
        };

        list.elementHeightCallback = index =>
        {
            if (index < 0 || index >= visibleIndices.Count)
                return lineHeight;

            var rule = rulesProp.GetArrayElementAtIndex(visibleIndices[index]);
            return Mathf.Max(lineHeight, GetRuleRowHeight(rule, lineHeight, spacing));
        };

        list.onAddCallback = _ =>
        {
            var index = rulesProp.arraySize;
            rulesProp.InsertArrayElementAtIndex(index);
            NormalizeRuleElement(rulesProp.GetArrayElementAtIndex(index), rulesProp);
            rulesState.SelectedRow = visibleIndices.Count;
            Repaint();
        };

        list.onRemoveCallback = _ =>
        {
            if (rulesState.SelectedRow < 0 && list.index >= 0 &&
                list.index < visibleIndices.Count)
            {
                rulesState.SelectedRow = list.index;
            }

            if (rulesState.SelectedRow < 0 || rulesState.SelectedRow >= visibleIndices.Count)
                return;

            rulesProp.DeleteArrayElementAtIndex(visibleIndices[rulesState.SelectedRow]);
            rulesState.SelectedRow = -1;
            Repaint();
        };

        list.onReorderCallback = _ => CommitReorder(previousOrder, visibleIndices, rulesProp);
        AttachListFooter(list, () => GetSelectedRowIndex(list, rulesState), index => DuplicateRulesElement(list, rulesState, rulesProp, index), _ => list.onAddCallback?.Invoke(list), () => list.onRemoveCallback?.Invoke(list));
        list.DoLayoutList();
    }

    private bool ToggleListExpandedLayout(SerializedProperty arrayProperty, string title)
    {
        var key = arrayProperty.propertyPath;
        if (!listFoldouts.TryGetValue(key, out var expanded)) expanded = true;
        var value = EditorGUILayout.Foldout(expanded, title + " (" + arrayProperty.arraySize + ")", true);
        if (value != expanded)
        {
            listFoldouts[key] = value;
        }

        return value;
    }

    private bool ToggleListExpandedRect(
        SerializedProperty arrayProperty,
        string title,
        float x,
        float width,
        ref float y)
    {
        var key = arrayProperty.propertyPath;
        if (!listFoldouts.TryGetValue(key, out var expanded)) expanded = true;
        var lineHeight = EditorGUIUtility.singleLineHeight;
        var spacing = EditorGUIUtility.standardVerticalSpacing;
        var value = EditorGUI.Foldout(
            new Rect(x, y, Mathf.Max(0f, width - 16f), lineHeight),
            expanded,
            title + " (" + arrayProperty.arraySize + ")",
            true);
        if (value != expanded)
        {
            listFoldouts[key] = value;
        }

        y += lineHeight + spacing;
        return value;
    }

    private bool ShouldShowCompactList(SerializedProperty arrayProperty, string title)
    {
        return !listFoldouts.TryGetValue(arrayProperty.propertyPath, out var expanded) || expanded;
    }

    private static void RefreshRuleIndices(
        EditableListState state,
        SerializedProperty rulesProp,
        string keyword)
    {
        state.VisibleIndices.Clear();
        for (var i = 0; i < rulesProp.arraySize; i++)
        {
            var element = rulesProp.GetArrayElementAtIndex(i);
            if (MatchesKeyword(DescribeRule(element), keyword))
                state.VisibleIndices.Add(i);
        }

        if (state.SelectedRow >= state.VisibleIndices.Count)
            state.SelectedRow = state.VisibleIndices.Count - 1;
    }

    private void DrawRuleRow(Rect rect, SerializedProperty rule, int rowIndex)
    {
        var lineHeight = EditorGUIUtility.singleLineHeight;
        var spacing = EditorGUIUtility.standardVerticalSpacing;

        const float dragGutter = 24f;
        var headerRect = new Rect(rect.x + dragGutter, rect.y, rect.width - dragGutter, lineHeight);
        rule.isExpanded = EditorGUI.Foldout(
            headerRect,
            rule.isExpanded,
            "[" + rowIndex + "] " + DescribeRuleHeading(rule),
            true);

        if (!rule.isExpanded)
            return;

        var x = rect.x + dragGutter;
        var y = rect.y + lineHeight + spacing;
        var width = rect.width - dragGutter;

        DrawPropertyRow(rule, "ruleId", x, ref y, width);
        DrawPropertyRow(rule, "minCount", x, ref y, width);
        DrawPropertyRow(rule, "maxCount", x, ref y, width);
        DrawPropertyRow(rule, "seed", x, ref y, width);
        DrawPropertyRow(rule, "repeatCount", x, ref y, width);
        DrawPropertyRow(rule, "repeatIntervalMin", x, ref y, width);
        DrawPropertyRow(rule, "repeatIntervalMax", x, ref y, width);

        DrawCandidatesEditor(x, ref y, width, rule);
        DrawBehaviorEditor(x, ref y, width, "Rule Behaviors", rule.FindPropertyRelative("behaviors"));
    }

    private float GetRuleRowHeight(SerializedProperty rule, float lineHeight, float spacing)
    {
        var height = lineHeight; // foldout header
        if (!rule.isExpanded)
            return height;

        height += spacing; // gap below the foldout header
        height += GetPropertyRowHeight(rule, "ruleId") + spacing;
        height += GetPropertyRowHeight(rule, "minCount") + spacing;
        height += GetPropertyRowHeight(rule, "maxCount") + spacing;
        height += GetPropertyRowHeight(rule, "seed") + spacing;
        height += GetPropertyRowHeight(rule, "repeatCount") + spacing;
        height += GetPropertyRowHeight(rule, "repeatIntervalMin") + spacing;
        height += GetPropertyRowHeight(rule, "repeatIntervalMax") + spacing;
        height += GetCandidatesEditorHeight(rule, lineHeight, spacing);
        
        height += GetBehaviorEditorHeight("Rule Behaviors", rule.FindPropertyRelative("behaviors"), lineHeight, spacing);
        return height;
    }

    private void DrawCandidatesEditor(float x, ref float y, float width, SerializedProperty rule)
    {
        var candidatesProp = rule.FindPropertyRelative("candidates");
        var lineHeight = EditorGUIUtility.singleLineHeight;
        var spacing = EditorGUIUtility.standardVerticalSpacing;
        if (candidatesProp == null || !candidatesProp.isArray)
            return;

        if (candidatesProp.arraySize == 0)
        {
            if (GUI.Button(new Rect(x, y, width, lineHeight), "+ 添加候选", EditorStyles.miniButton))
            {
                candidatesProp.InsertArrayElementAtIndex(candidatesProp.arraySize);
                NormalizeCandidateElement(candidatesProp.GetArrayElementAtIndex(candidatesProp.arraySize - 1));
                Repaint();
            }

            y += lineHeight + spacing;
            return;
        }

        if (!ToggleListExpandedRect(candidatesProp, "Candidates", x, width, ref y)) return;

        var stateKey = "candidates:" + candidatesProp.propertyPath;
        var state = GetOrCreateListState(stateKey);
        var listHeight = GetCandidatesReorderableListHeight(candidatesProp, lineHeight, spacing);
        var list = new ReorderableList(serializedObject, candidatesProp, true, false, true, true);

        list.drawElementCallback = (rect, rowIndex, isActive, _) =>
        {
            if (rowIndex < 0 || rowIndex >= candidatesProp.arraySize) return;

            if (isActive)
            {
                state.SelectedRow = rowIndex;
                list.index = rowIndex;
            }

            var previousSelectedRow = state.SelectedRow;
            TrackRowSelection(state, rect, rowIndex);
            if (state.SelectedRow != previousSelectedRow)
                list.index = rowIndex;

            DrawSelectionBackground(state, rect, rowIndex);

            var candidate = candidatesProp.GetArrayElementAtIndex(rowIndex);
            DrawCandidateRow(rect, candidate, rowIndex, candidatesProp);
        };

        list.elementHeightCallback = rowIndex =>
        {
            if (rowIndex < 0 || rowIndex >= candidatesProp.arraySize)
                return lineHeight;

            var candidate = candidatesProp.GetArrayElementAtIndex(rowIndex);
            return Mathf.Max(lineHeight, GetCandidateRowHeight(candidate, lineHeight, spacing));
        };

        list.onAddCallback = _ =>
        {
            var index = candidatesProp.arraySize;
            candidatesProp.InsertArrayElementAtIndex(index);
            NormalizeCandidateElement(candidatesProp.GetArrayElementAtIndex(index));
            state.SelectedRow = index;
            Repaint();
        };

        list.onRemoveCallback = removedList =>
        {
            if (state.SelectedRow < 0 && removedList.index >= 0 && removedList.index < candidatesProp.arraySize)
                state.SelectedRow = removedList.index;

            if (state.SelectedRow < 0 || state.SelectedRow >= candidatesProp.arraySize)
                return;

            candidatesProp.DeleteArrayElementAtIndex(state.SelectedRow);
            serializedObject.ApplyModifiedProperties();
            Repaint();
            state.SelectedRow = -1;
            EditorUtility.SetDirty(target);
            GUIUtility.ExitGUI();
        };

        AttachListFooter(list, () => GetSelectedRowIndex(list, state), index => DuplicateCandidateElement(list, state, candidatesProp, index), _ => list.onAddCallback?.Invoke(list), () => list.onRemoveCallback?.Invoke(list));
        list.DoList(new Rect(x, y, width, listHeight));
        y += listHeight + spacing;
    }

    private float GetCandidatesEditorHeight(
        SerializedProperty rule,
        float lineHeight,
        float spacing)
    {
        var candidatesProp = rule.FindPropertyRelative("candidates");
        if (candidatesProp == null || !candidatesProp.isArray)
            return 0f;

        if (candidatesProp.arraySize == 0)
            return lineHeight + spacing;

        var height = lineHeight + spacing; // header row
        if (!ShouldShowCompactList(candidatesProp, "Candidates"))
            return height;

        height += GetCandidatesReorderableListHeight(candidatesProp, lineHeight, spacing);
        return height + spacing;
    }

    private float GetCandidatesReorderableListHeight(
        SerializedProperty candidatesProp,
        float lineHeight,
        float spacing)
    {
        var height = 0f;
        for (var i = 0; i < candidatesProp.arraySize; i++)
        {
            var candidate = candidatesProp.GetArrayElementAtIndex(i);
            height += Mathf.Max(lineHeight, GetCandidateRowHeight(candidate, lineHeight, spacing));
            height += spacing;
        }

        height += lineHeight + 6f + 6f; // ReorderableList +/- footer
        return Mathf.Max(lineHeight, height);
    }
    private void DrawCandidateRow(Rect rect, SerializedProperty candidate, int rowIndex,
        SerializedProperty candidatesProp)
    {
        var lineHeight = EditorGUIUtility.singleLineHeight;
        var spacing = EditorGUIUtility.standardVerticalSpacing;

        var assetpath = candidate.FindPropertyRelative("assetpath")?.stringValue ?? string.Empty;
        var header = string.IsNullOrEmpty(assetpath) ? "<no assetpath>" : assetpath;

        candidate.isExpanded = EditorGUI.Foldout(
            new Rect(rect.x + 2f, rect.y, rect.width - 24f, lineHeight),
            candidate.isExpanded,
            "[" + rowIndex + "] " + header,
            true);


        if (!candidate.isExpanded)
            return;

        var x = rect.x;
        var y = rect.y + lineHeight + spacing;
        var width = rect.width;

        DrawPropertyRow(candidate, "assetpath", x, ref y, width);
        DrawPrefabPickerRow(x, ref y, width, candidate);
        DrawPropertyRow(candidate, "weight", x, ref y, width);
        DrawPropertyRow(candidate, "localScale", x, ref y, width);
        DrawPropertyRow(candidate, "localEulerAngles", x, ref y, width);
        DrawPropertyRow(candidate, "uniqueAnchors", x, ref y, width);
        DrawPropertyRow(candidate, "lifetime", x, ref y, width);
        DrawPropertyRow(candidate, "destroyWhenBehaviorsFinished", x, ref y, width);

        if (!CandidateHasRoute(candidate))
        {
            if (GUI.Button(new Rect(x, y, width, lineHeight), "Scene Edit", EditorStyles.miniButton))
                BeginCandidateAnchorSceneEdit(candidate);
            y += lineHeight + spacing;

            DrawVector3ArrayEditor("anchorPoints", candidate.FindPropertyRelative("anchorPoints"), x, ref y, width);
        }

        DrawBehaviorEditor(x, ref y, width, "Behaviors", candidate.FindPropertyRelative("behaviors"));
    }

    private float GetCandidateRowHeight(SerializedProperty candidate, float lineHeight, float spacing)
    {
        var height = lineHeight; // row header
        if (!candidate.isExpanded)
            return height;

        height += spacing;
        height += GetPropertyRowHeight(candidate, "assetpath") + spacing;
        height += lineHeight + spacing; // prefab picker row
        height += GetPropertyRowHeight(candidate, "weight") + spacing;
        height += GetPropertyRowHeight(candidate, "localScale") + spacing;
        height += GetPropertyRowHeight(candidate, "localEulerAngles") + spacing;
        height += GetPropertyRowHeight(candidate, "uniqueAnchors") + spacing;
        height += GetPropertyRowHeight(candidate, "lifetime") + spacing;
        height += GetPropertyRowHeight(candidate, "destroyWhenBehaviorsFinished") + spacing;

        if (!CandidateHasRoute(candidate))
        {
            height += lineHeight + spacing; // Scene Edit button
            height += GetVector3ArrayBlockHeight(candidate.FindPropertyRelative("anchorPoints"), lineHeight, spacing);
        }

        height += GetBehaviorEditorHeight("Behaviors", candidate.FindPropertyRelative("behaviors"), lineHeight, spacing);
        return height;
    }

    private void DrawBehaviorEditor(float x, ref float y, float width, string title, SerializedProperty behaviors)
    {
        if (behaviors == null)
            return;

        var lineHeight = EditorGUIUtility.singleLineHeight;
        var spacing = EditorGUIUtility.standardVerticalSpacing;
        if (behaviors.arraySize == 0)
        {
            var addButtonRect = new Rect(x, y, width, lineHeight);
            if (GUI.Button(addButtonRect, "+ 添加行为", EditorStyles.miniButton))
            {
                ShowBehaviorTypeMenu(addButtonRect, behaviors);
            }

            y += lineHeight + spacing;
            return;
        }

        if (!ToggleListExpandedRect(behaviors, title, x, width, ref y)) return;

        var stateKey = "behavior:" + behaviors.propertyPath;
        var state = GetOrCreateListState(stateKey);
        RefreshVisibleIndices(state, behaviors, string.Empty);

        var listHeight = GetBehaviorReorderableListHeight(state, behaviors, lineHeight, spacing);
        var previousOrder = new List<int>(state.VisibleIndices);
        var reorderableList = new ReorderableList(
            state.VisibleIndices,
            typeof(int),
            true,
            false,
            true,
            true);

        reorderableList.drawElementCallback = (rect, rowIndex, isActive, _) =>
        {
            if (rowIndex < 0 || rowIndex >= state.VisibleIndices.Count) return;

            if (isActive)
            {
                state.SelectedRow = rowIndex;
                reorderableList.index = rowIndex;
            }

            var previousSelectedRow = state.SelectedRow;
            TrackRowSelection(state, rect, rowIndex);
            if (state.SelectedRow != previousSelectedRow)
                reorderableList.index = rowIndex;

            DrawSelectionBackground(state, rect, rowIndex);

            var behavior = behaviors.GetArrayElementAtIndex(state.VisibleIndices[rowIndex]);
            DrawBehaviorRow(rect, behavior);
        };

        reorderableList.elementHeightCallback = rowIndex =>
        {
            if (rowIndex < 0 || rowIndex >= state.VisibleIndices.Count)
                return lineHeight;

            var behavior = behaviors.GetArrayElementAtIndex(state.VisibleIndices[rowIndex]);
            return Mathf.Max(lineHeight, GetBehaviorRowHeight(behavior));
        };

        reorderableList.onAddDropdownCallback = (buttonRect, _) => { ShowBehaviorTypeMenu(buttonRect, behaviors); };

        reorderableList.onRemoveCallback = removedList =>
        {
            if (state.SelectedRow < 0 && removedList.index >= 0 &&
                removedList.index < state.VisibleIndices.Count)
            {
                state.SelectedRow = removedList.index;
            }

            if (state.SelectedRow < 0 || state.SelectedRow >= state.VisibleIndices.Count)
                return;

            behaviors.DeleteArrayElementAtIndex(state.VisibleIndices[state.SelectedRow]);
            serializedObject.ApplyModifiedProperties();
            Repaint();
            state.SelectedRow = -1;
            EditorUtility.SetDirty(target);
            GUIUtility.ExitGUI();
        };

        reorderableList.onReorderCallback = _ => { CommitReorder(previousOrder, state.VisibleIndices, behaviors); };

        AttachListFooter(reorderableList, () => GetSelectedRowIndex(reorderableList, state), index => DuplicateBehaviorElement(reorderableList, state, behaviors, index), buttonRect => ShowBehaviorTypeMenu(buttonRect, behaviors), () => reorderableList.onRemoveCallback?.Invoke(reorderableList));
        reorderableList.DoList(new Rect(x, y, width, listHeight));
        y += listHeight + spacing;
    }

    private float GetBehaviorEditorHeight(
        string title,
        SerializedProperty behaviors,
        float lineHeight,
        float spacing)
    {
        if (behaviors == null)
            return 0f;

        if (behaviors.arraySize == 0)
            return lineHeight + spacing;

        var stateKey = "behavior:" + behaviors.propertyPath;
        var state = GetOrCreateListState(stateKey);
        RefreshVisibleIndices(state, behaviors, string.Empty);

        var height = lineHeight + spacing; // header row
        if (!ShouldShowCompactList(behaviors, title))
            return height;

        height += GetBehaviorReorderableListHeight(state, behaviors, lineHeight, spacing);
        return height + spacing;
    }

    private float GetBehaviorReorderableListHeight(
        EditableListState state,
        SerializedProperty behaviors,
        float lineHeight,
        float spacing)
    {
        var height = 0f;
        for (var i = 0; i < state.VisibleIndices.Count; i++)
        {
            var behavior = behaviors.GetArrayElementAtIndex(state.VisibleIndices[i]);
            height += Mathf.Max(lineHeight, GetBehaviorRowHeight(behavior));
            height += spacing;
        }

        height += EditorGUIUtility.singleLineHeight + 6f; // ReorderableList +/- footer
        return Mathf.Max(lineHeight, height);
    }
    private void DrawBehaviorRow(Rect rect, SerializedProperty behavior)
    {
        var lineHeight = EditorGUIUtility.singleLineHeight;
        var spacing = EditorGUIUtility.standardVerticalSpacing;

        var label = GetBehaviorTypeName(behavior);
        behavior.isExpanded = EditorGUI.Foldout(
            new Rect(rect.x, rect.y, rect.width, lineHeight),
            behavior.isExpanded,
            label,
            true);

        if (!behavior.isExpanded)
            return;
        var y = rect.y + lineHeight + spacing;
        var childScope = behavior.propertyPath + ".";
        var child = behavior.Copy();
        var hasChild = child.NextVisible(true);
        while (hasChild && child.propertyPath.StartsWith(childScope, StringComparison.Ordinal))
        {
            if (child.name == "waypoints")
            {
                DrawVector3ArrayEditor("Waypoints", child, rect.x, ref y, rect.width);
            }
            else
            {
                var height = EditorGUI.GetPropertyHeight(child, true);
                EditorGUI.PropertyField(new Rect(rect.x, y, rect.width, height), child, true);
                y += height + spacing;
            }
        
            hasChild = child.NextVisible(false);
        }
    }

    private float GetBehaviorRowHeight(SerializedProperty behavior)
    {
        var lineHeight = EditorGUIUtility.singleLineHeight;
        var spacing = EditorGUIUtility.standardVerticalSpacing;

        if (!behavior.isExpanded)
            return lineHeight + 2f;

        var height = lineHeight + spacing;

        // 与绘制保持一致；propertyPath 前缀阻止迭代器逃出 managedReference 作用域。
        var childScope = behavior.propertyPath + ".";
        var child = behavior.Copy();
        var hasChild = child.NextVisible(true);
        while (hasChild && child.propertyPath.StartsWith(childScope, StringComparison.Ordinal))
        {
            if (child.name == "waypoints")
            {
                height += GetVector3ArrayBlockHeight(child, lineHeight, spacing);
            }
            else
            {
                height += EditorGUI.GetPropertyHeight(child, true) + spacing;
            }

            hasChild = child.NextVisible(false);
        }

        return height;
    }

    private void DrawVector3ArrayEditor(
        string title,
        SerializedProperty arrayProp,
        float x,
        ref float y,
        float width)
    {
        if (arrayProp == null || !arrayProp.isArray)
            return;

        var lineHeight = EditorGUIUtility.singleLineHeight;
        var spacing = EditorGUIUtility.standardVerticalSpacing;
        if (arrayProp.arraySize == 0)
        {
            if (GUI.Button(new Rect(x, y, width, lineHeight), "+ " + title, EditorStyles.miniButton))
            {
                arrayProp.InsertArrayElementAtIndex(arrayProp.arraySize);
                arrayProp.GetArrayElementAtIndex(arrayProp.arraySize - 1).vector3Value = Vector3.zero;
            }

            y += lineHeight + spacing;
            return;
        }

        if (!ToggleVector3ArrayExpandedRect(arrayProp, title, x, width, ref y)) return;

        var stateKey = "vector3:" + arrayProp.propertyPath;
        var state = GetOrCreateListState(stateKey);
        if (state.SelectedRow >= arrayProp.arraySize)
            state.SelectedRow = -1;

        var listHeight = GetVector3ArrayListHeight(arrayProp, lineHeight, spacing);
        var list = new ReorderableList(serializedObject, arrayProp, true, false, true, true);

        list.drawElementCallback = (rect, index, isActive, _) =>
        {
            if (index < 0 || index >= arrayProp.arraySize)
                return;

            if (isActive)
                state.SelectedRow = index;

            TrackRowSelection(state, rect, index);
            DrawSelectionBackground(state, rect, index);

            var element = arrayProp.GetArrayElementAtIndex(index);
            element.isExpanded = false;
            EditorGUI.PropertyField(rect, element, GUIContent.none, false);
        };

        list.elementHeightCallback = _ => lineHeight;

        list.onAddCallback = _ =>
        {
            var index = arrayProp.arraySize;
            arrayProp.InsertArrayElementAtIndex(index);
            arrayProp.GetArrayElementAtIndex(index).vector3Value = Vector3.zero;
        };

        list.onRemoveCallback = _ =>
        {
            var index = state.SelectedRow;
            if (index < 0 || index >= arrayProp.arraySize) return;

            arrayProp.DeleteArrayElementAtIndex(index);
            state.SelectedRow = -1;
            list.index = -1;
            Repaint();
        };

        AttachListFooter(list, () => GetSelectedRowIndex(list, state), index => DuplicateVector3Element(list, state, arrayProp, index), _ => list.onAddCallback?.Invoke(list), () => list.onRemoveCallback?.Invoke(list));
        list.DoList(new Rect(x, y, width, listHeight));
        y += listHeight + spacing;
    }

    private bool ToggleVector3ArrayExpandedRect(
        SerializedProperty arrayProperty,
        string title,
        float x,
        float width,
        ref float y)
    {
        var lineHeight = EditorGUIUtility.singleLineHeight;
        var spacing = EditorGUIUtility.standardVerticalSpacing;
        var showReverseButton = title == "Waypoints";
        var buttonWidth = showReverseButton ? 58f : 0f;
        var foldoutWidth = Mathf.Max(0f, width - buttonWidth - 2f);

        var key = arrayProperty.propertyPath;
        if (!listFoldouts.TryGetValue(key, out var expanded)) expanded = true;
        var value = EditorGUI.Foldout(
            new Rect(x, y, foldoutWidth, lineHeight),
            expanded,
            title + " (" + arrayProperty.arraySize + ")",
            true);
        if (value != expanded)
        {
            listFoldouts[key] = value;
        }

        if (showReverseButton)
        {
            var buttonRect = new Rect(x + foldoutWidth + 2f, y, buttonWidth, lineHeight);
            if (GUI.Button(buttonRect, "Rev", EditorStyles.miniButton))
                ReverseVector3Array(arrayProperty);
        }

        y += lineHeight + spacing;
        return value;
    }

    private static void ReverseVector3Array(SerializedProperty arrayProperty)
    {
        if (arrayProperty == null || !arrayProperty.isArray || arrayProperty.arraySize < 2) return;

        var count = arrayProperty.arraySize;
        var values = new Vector3[count];
        for (var i = 0; i < count; i++)
            values[i] = arrayProperty.GetArrayElementAtIndex(i).vector3Value;

        Array.Reverse(values);
        for (var i = 0; i < count; i++)
            arrayProperty.GetArrayElementAtIndex(i).vector3Value = values[i];
    }
    private float GetVector3ArrayListHeight(
        SerializedProperty arrayProp,
        float lineHeight,
        float spacing)
    {
        var height = 0f;
        for (var i = 0; i < arrayProp.arraySize; i++)
        {
            height += lineHeight;
            height += spacing;
        }

        height += lineHeight + 6f + 6f; // ReorderableList +/- footer
        return Mathf.Max(lineHeight, height);
    }

    private float GetVector3ArrayBlockHeight(
        SerializedProperty arrayProp,
        float lineHeight,
        float spacing)
    {
        if (arrayProp == null || !arrayProp.isArray)
            return 0f;

        if (arrayProp.arraySize == 0)
            return lineHeight + spacing;

        var height = lineHeight + spacing; // header row
        if (!ShouldShowCompactList(arrayProp, string.Empty))
            return height;

        height += GetVector3ArrayListHeight(arrayProp, lineHeight, spacing);
        return height + spacing;
    }


    private static void DrawPrefabPickerRow(float x, ref float y, float width, SerializedProperty candidate)
    {
        var lineHeight = EditorGUIUtility.singleLineHeight;
        var spacing = EditorGUIUtility.standardVerticalSpacing;

        var picked = (GameObject)EditorGUI.ObjectField(
            new Rect(x, y, width, lineHeight),
            null,
            typeof(GameObject),
            false);

        y += lineHeight + spacing;

        if (picked == null) return;

        var path = AssetDatabase.GetAssetPath(picked);
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            return;

        var assetpath = candidate.FindPropertyRelative("assetpath");
        if (assetpath != null) assetpath.stringValue = path;
    }

    private void ShowBehaviorTypeMenu(Rect buttonRect, SerializedProperty behaviors)
    {
        EnsureBehaviorTypes();
        if (behaviorTypes.Length == 0)
        {
            EditorUtility.DisplayDialog(
                "Map Presentation Baker",
                "No concrete MapPresentationBehavior subclass found.",
                "OK");
            return;
        }

        var menu = new GenericMenu();
        for (var i = 0; i < behaviorTypes.Length; i++)
        {
            var type = behaviorTypes[i];
            if (ShouldHideBehaviorMenuItem(behaviors, type)) continue;

            menu.AddItem(new GUIContent(type.Name), false, () => AddBehavior(behaviors, type));
        }

        menu.DropDown(buttonRect);
    }

    private static bool ShouldHideBehaviorMenuItem(SerializedProperty behaviors, Type menuItemType)
    {
        var existingTypes = GetExistingBehaviorTypes(behaviors);
        if (existingTypes.Contains(menuItemType))
            return Attribute.GetCustomAttribute(
                menuItemType,
                typeof(MultiplePresentationBehaviorAttribute),
                true) == null;

        if (!(Attribute.GetCustomAttribute(
                menuItemType,
                typeof(MutexPresentationBehaviorAttribute),
                true) is MutexPresentationBehaviorAttribute mutexAttribute) ||
            mutexAttribute.mutexTypes == null)
        {
            return false;
        }

        foreach (var mutexType in mutexAttribute.mutexTypes)
        {
            if (existingTypes.Contains(mutexType)) return true;
        }

        return false;
    }

    private static HashSet<Type> GetExistingBehaviorTypes(SerializedProperty behaviors)
    {
        var types = new HashSet<Type>();
        if (behaviors == null || !behaviors.isArray) return types;

        for (var i = 0; i < behaviors.arraySize; i++)
        {
            var behavior = behaviors.GetArrayElementAtIndex(i).managedReferenceValue;
            if (behavior != null) types.Add(behavior.GetType());
        }

        return types;
    }
    private void AddBehavior(SerializedProperty behaviors, Type type)
    {
        var index = behaviors.arraySize;
        behaviors.InsertArrayElementAtIndex(index);
        behaviors.GetArrayElementAtIndex(index).managedReferenceValue =
            Activator.CreateInstance(type);

        serializedObject.ApplyModifiedProperties();
        Repaint();

        var stateKey = "behavior:" + behaviors.propertyPath;
        GetOrCreateListState(stateKey).SelectedRow = index;
        EditorUtility.SetDirty(target);
    }

    private void AttachListFooter(
        ReorderableList list,
        Func<int> selectedIndexProvider,
        Action<int> duplicate,
        Action<Rect> add,
        Action remove)
    {
        list.displayAdd = false;
        list.displayRemove = false;
        list.footerHeight = EditorGUIUtility.singleLineHeight + 6f;
        list.drawFooterCallback = rect =>
        {
            var selectedIndex = selectedIndexProvider();
            var spacing = 2f;
            var buttonHeight = EditorGUIUtility.singleLineHeight;
            var buttonWidth = Mathf.Max(0f, (rect.width - spacing * 2f) / 3f);
            var y = rect.y + 2f;
            var x = rect.x;

            using (new EditorGUI.DisabledScope(add == null))
            {
                if (GUI.Button(new Rect(x, y, buttonWidth, buttonHeight), "add", EditorStyles.miniButton))
                    add?.Invoke(new Rect(x, y, buttonWidth, buttonHeight));
            }

            x += buttonWidth + spacing;
            using (new EditorGUI.DisabledScope(selectedIndex < 0 || remove == null))
            {
                if (GUI.Button(new Rect(x, y, buttonWidth, buttonHeight), "remove", EditorStyles.miniButton))
                    remove?.Invoke();
            }

            x += buttonWidth + spacing;
            using (new EditorGUI.DisabledScope(selectedIndex < 0 || duplicate == null))
            {
                if (GUI.Button(new Rect(x, y, buttonWidth, buttonHeight), "Copy", EditorStyles.miniButton))
                    duplicate?.Invoke(selectedIndex);
            }
        };
    }

    private static int GetSelectedRowIndex(ReorderableList list, EditableListState state)
    {
        return state != null ? state.SelectedRow : list.index;
    }

    private static void SetSelectedRowIndex(ReorderableList list, EditableListState state, int index)
    {
        if (state != null) state.SelectedRow = index;
        list.index = index;
    }

    private static void CloneSerializeReferenceBehaviors(SerializedProperty owner)
    {
        var behaviors = owner.FindPropertyRelative("behaviors");
        if (behaviors != null && behaviors.isArray)
        {
            for (var i = 0; i < behaviors.arraySize; i++)
            {
                var element = behaviors.GetArrayElementAtIndex(i);
                if (element.managedReferenceValue is MapPresentationBehavior behavior)
                    element.managedReferenceValue = behavior.Clone();
            }
        }

        var candidates = owner.FindPropertyRelative("candidates");
        if (candidates == null || !candidates.isArray) return;

        for (var i = 0; i < candidates.arraySize; i++)
            CloneSerializeReferenceBehaviors(candidates.GetArrayElementAtIndex(i));
    }
    private static bool IsValidArrayIndex(SerializedProperty arrayProperty, int index)
    {
        return arrayProperty != null && arrayProperty.isArray &&
               index >= 0 && index < arrayProperty.arraySize;
    }

    private void DuplicateRulesElement(
        ReorderableList list,
        EditableListState state,
        SerializedProperty rulesProp,
        int index)
    {
        if (!IsValidArrayIndex(rulesProp, index)) return;

        var newIndex = index + 1;
        rulesProp.InsertArrayElementAtIndex(newIndex);
        CloneSerializeReferenceBehaviors(rulesProp.GetArrayElementAtIndex(newIndex));
        SetSelectedRowIndex(list, state, newIndex);
        serializedObject.ApplyModifiedProperties();
        EditorUtility.SetDirty(target);
        Repaint();
    }

    private void DuplicateCandidateElement(
        ReorderableList list,
        EditableListState state,
        SerializedProperty candidatesProp,
        int index)
    {
        if (!IsValidArrayIndex(candidatesProp, index)) return;

        var newIndex = index + 1;
        candidatesProp.InsertArrayElementAtIndex(newIndex);
        CloneSerializeReferenceBehaviors(candidatesProp.GetArrayElementAtIndex(newIndex));
        SetSelectedRowIndex(list, state, newIndex);
        serializedObject.ApplyModifiedProperties();
        EditorUtility.SetDirty(target);
        Repaint();
    }

    private void DuplicateBehaviorElement(
        ReorderableList list,
        EditableListState state,
        SerializedProperty behaviors,
        int index)
    {
        if (!IsValidArrayIndex(behaviors, index)) return;

        var source = behaviors.GetArrayElementAtIndex(index).managedReferenceValue
            as MapPresentationBehavior;
        if (source == null) return;

        var clone = source.Clone();
        var newIndex = index + 1;
        behaviors.InsertArrayElementAtIndex(newIndex);
        behaviors.GetArrayElementAtIndex(newIndex).managedReferenceValue = clone;
        SetSelectedRowIndex(list, state, newIndex);
        serializedObject.ApplyModifiedProperties();
        EditorUtility.SetDirty(target);
        Repaint();
    }

    private void DuplicateVector3Element(
        ReorderableList list,
        EditableListState state,
        SerializedProperty arrayProp,
        int index)
    {
        if (!IsValidArrayIndex(arrayProp, index)) return;

        var sourceValue = arrayProp.GetArrayElementAtIndex(index).vector3Value;
        var newIndex = index + 1;
        arrayProp.InsertArrayElementAtIndex(newIndex);
        arrayProp.GetArrayElementAtIndex(newIndex).vector3Value = sourceValue;
        SetSelectedRowIndex(list, state, newIndex);
    }
    private EditableListState GetOrCreateListState(string key)
    {
        if (!listStates.TryGetValue(key, out var state))
        {
            state = new EditableListState();
            listStates[key] = state;
        }

        return state;
    }

    private static void RefreshVisibleIndices(
        EditableListState state,
        SerializedProperty arrayProperty,
        string keyword)
    {
        state.VisibleIndices.Clear();

        for (var i = 0; i < arrayProperty.arraySize; i++)
        {
            var element = arrayProperty.GetArrayElementAtIndex(i);
            if (MatchesKeyword(DescribeBehavior(element), keyword))
                state.VisibleIndices.Add(i);
        }

        if (state.SelectedRow >= state.VisibleIndices.Count)
            state.SelectedRow = state.VisibleIndices.Count - 1;
    }

    private static void TrackRowSelection(EditableListState state, Rect rowRect, int rowIndex)
    {
        var current = Event.current;
        if (current.type != EventType.MouseDown || current.button != 0)
            return;

        if (!rowRect.Contains(current.mousePosition))
            return;

        state.SelectedRow = rowIndex;
    }

    private static void DrawSelectionBackground(EditableListState state, Rect rowRect, int rowIndex)
    {
        if (state.SelectedRow != rowIndex)
            return;

        EditorGUI.DrawRect(rowRect, SelectedRowColor);
    }

    private static void DrawPropertyRow(
        SerializedProperty owner,
        string fieldName,
        float x,
        ref float y,
        float width)
    {
        var property = owner.FindPropertyRelative(fieldName);
        if (property == null)
        {
            Debug.LogError($"Not Found:{fieldName} Parent:{owner.name}");
            return;
        }

        var height = EditorGUI.GetPropertyHeight(property, true);
        EditorGUI.PropertyField(new Rect(x, y, width, height), property, true);
        y += height + EditorGUIUtility.standardVerticalSpacing;
    }

    private static float GetPropertyRowHeight(SerializedProperty owner, string fieldName)
    {
        var property = owner.FindPropertyRelative(fieldName);
        return property == null
            ? 0f
            : EditorGUI.GetPropertyHeight(property, true);
    }

    private static string GetBehaviorTypeName(SerializedProperty behavior)
    {
        return behavior.managedReferenceValue is MapPresentationBehavior instance
            ? instance.GetType().Name
            : "null";
    }

    private static string DescribeBehavior(SerializedProperty property)
    {
        return property.managedReferenceValue is MapPresentationBehavior behavior
            ? behavior.GetType().Name
            : "null";
    }

    private static string DescribeRuleHeading(SerializedProperty rule)
    {
        if (rule == null) return "<invalid>";

        var candidates = rule.FindPropertyRelative("candidates");
        var behaviors = rule.FindPropertyRelative("behaviors");

        return "rule " + (rule.FindPropertyRelative("ruleId")?.intValue ?? 0)
                       + " | candidates:" + (candidates?.arraySize ?? 0)
                       + " | behaviors:" + (behaviors?.arraySize ?? 0);
    }

    private static void BeginCandidateAnchorSceneEdit(SerializedProperty candidate)
    {
        const string rulesToken = "rules.Array.data[";
        const string candidatesToken = ".candidates.Array.data[";

        var path = candidate?.propertyPath ?? string.Empty;
        var ruleStart = path.IndexOf(rulesToken, StringComparison.Ordinal);
        if (ruleStart < 0) return;

        var ruleEnd = path.IndexOf(']', ruleStart);
        var candidateStart = path.IndexOf(candidatesToken, ruleStart, StringComparison.Ordinal);
        if (ruleEnd < 0 || candidateStart < 0) return;

        candidateStart += candidatesToken.Length;
        var candidateEnd = path.IndexOf(']', candidateStart);
        if (candidateEnd < 0) return;

        if (!int.TryParse(path.Substring(ruleStart + rulesToken.Length, ruleEnd - ruleStart - rulesToken.Length), out var ruleIndex) ||
            !int.TryParse(path.Substring(candidateStart, candidateEnd - candidateStart), out var candidateIndex))
        {
            return;
        }

        if (candidate.serializedObject.targetObject is not MapPresentationBaker baker ||
            baker.rules == null || ruleIndex >= baker.rules.Length) return;

        var target = baker.rules[ruleIndex]?.candidates;
        if (target == null || candidateIndex >= target.Length) return;

        sceneBaker = baker;
        sceneSelectedCandidate = target[candidateIndex];
        sceneSelectedCandidateIndex = -1;
        sceneSelectedAnchor = -1;
        sceneAnchorMode = true;
        sceneOverlayCollapsed = false;
        sceneArmAddAnchor = false;
        SceneView.RepaintAll();
    }
    private static bool CandidateHasRoute(SerializedProperty candidate)
    {
        var behaviors = candidate?.FindPropertyRelative("behaviors");
        if (behaviors == null) return false;

        for (var i = 0; i < behaviors.arraySize; i++)
        {
            if (behaviors.GetArrayElementAtIndex(i).managedReferenceValue is MapPresentationRoute)
                return true;
        }

        return false;
    }

    private static string DescribeRule(SerializedProperty rule)
    {
        if (rule == null) return string.Empty;

        var builder = new StringBuilder();
        builder.Append(rule.FindPropertyRelative("ruleId")?.intValue ?? 0).Append(';');

        var candidates = rule.FindPropertyRelative("candidates");
        if (candidates != null)
        {
            for (var i = 0; i < candidates.arraySize; i++)
            {
                builder.Append(candidates.GetArrayElementAtIndex(i)
                    .FindPropertyRelative("assetpath")?.stringValue ?? string.Empty);
                builder.Append(';');
            }
        }

        var behaviors = rule.FindPropertyRelative("behaviors");
        if (behaviors != null)
        {
            for (var i = 0; i < behaviors.arraySize; i++)
            {
                builder.Append(GetBehaviorTypeName(behaviors.GetArrayElementAtIndex(i)));
                builder.Append(';');
            }
        }

        return builder.ToString();
    }

    private static void NormalizeRuleElement(SerializedProperty rule, SerializedProperty rulesProp)
    {
        SetEmptyArray(rule, "candidates");
        SetEmptyArray(rule, "behaviors");

        var maxId = -1;
        for (var i = 0; i < rulesProp.arraySize - 1; i++)
        {
            var ruleId = rulesProp.GetArrayElementAtIndex(i)
                .FindPropertyRelative("ruleId")?.intValue ?? -1;
            maxId = Math.Max(maxId, ruleId);
        }

        var newRuleId = rule.FindPropertyRelative("ruleId");
        if (newRuleId != null) newRuleId.intValue = maxId + 1;

        var minCount = rule.FindPropertyRelative("minCount");
        if (minCount != null) minCount.intValue = 1;

        var maxCount = rule.FindPropertyRelative("maxCount");
        if (maxCount != null) maxCount.intValue = 1;


        var seed = rule.FindPropertyRelative("seed");
        if (seed != null) seed.intValue = 0;

        var repeatCount = rule.FindPropertyRelative("repeatCount");
        if (repeatCount != null) repeatCount.intValue = 1;

        var repeatIntervalMin = rule.FindPropertyRelative("repeatIntervalMin");
        if (repeatIntervalMin != null) repeatIntervalMin.floatValue = 1f;

        var repeatIntervalMax = rule.FindPropertyRelative("repeatIntervalMax");
        if (repeatIntervalMax != null) repeatIntervalMax.floatValue = 1f;
    }

    private static void NormalizeCandidateElement(SerializedProperty candidate)
    {
        SetEmptyArray(candidate, "anchorPoints");
        SetEmptyArray(candidate, "behaviors");

        var assetpath = candidate.FindPropertyRelative("assetpath");
        if (assetpath != null) assetpath.stringValue = string.Empty;

        var uniqueAnchors = candidate.FindPropertyRelative("uniqueAnchors");
        if (uniqueAnchors != null) uniqueAnchors.boolValue = true;

        var weight = candidate.FindPropertyRelative("weight");
        if (weight != null) weight.floatValue = 1f;

        var scale = candidate.FindPropertyRelative("localScale");
        if (scale != null) scale.vector3Value = Vector3.one;

        var euler = candidate.FindPropertyRelative("localEulerAngles");
        if (euler != null) euler.vector3Value = Vector3.zero;

        var lifetime = candidate.FindPropertyRelative("lifetime");
        if (lifetime != null) lifetime.floatValue = 0f;

        var destroy = candidate.FindPropertyRelative("destroyWhenBehaviorsFinished");
        if (destroy != null) destroy.boolValue = true;
    }

    private static void SetEmptyArray(SerializedProperty owner, string fieldName)
    {
        var property = owner.FindPropertyRelative(fieldName);
        if (property != null && property.isArray)
            property.arraySize = 0;
    }

    private static bool MatchesKeyword(string text, string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return true;

        return text.IndexOf(keyword.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void CommitReorder(List<int> previousOrder, List<int> currentOrder, SerializedProperty arrayProperty)
    {
        if (previousOrder.Count == 0 || previousOrder.Count != currentOrder.Count)
            return;

        if (currentOrder.Count != arrayProperty.arraySize)
            return;

        var logicalPositions = new List<int>(previousOrder);

        for (var i = 0; i < currentOrder.Count; i++)
        {
            var sourceSlot = logicalPositions.IndexOf(currentOrder[i]);
            if (sourceSlot < 0) return;

            if (sourceSlot != i)
                arrayProperty.MoveArrayElement(sourceSlot, i);

            ShiftLogicalPosition(logicalPositions, sourceSlot, i);
        }
    }

    private static void ShiftLogicalPosition(List<int> positions, int from, int to)
    {
        if (from == to) return;

        var value = positions[from];
        if (from < to)
        {
            for (var i = from; i < to; i++)
                positions[i] = positions[i + 1];
        }
        else
        {
            for (var i = from; i > to; i--)
                positions[i] = positions[i - 1];
        }

        positions[to] = value;
    }

    private static void EnsureBehaviorTypes()
    {
        if (behaviorTypes.Length > 0) return;

        var types = new List<Type>();
        foreach (var type in TypeCache.GetTypesDerivedFrom<MapPresentationBehavior>())
        {
            if (type.IsAbstract || type.IsGenericTypeDefinition) continue;
            if (type.GetConstructor(Type.EmptyTypes) == null) continue;

            types.Add(type);
        }

        types.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        behaviorTypes = types.ToArray();
    }

    private sealed class EditableListState
    {
        public readonly List<int> VisibleIndices = new List<int>();
        public int SelectedRow = -1;
    }
    #region Scene Route Editing (draft)

    // State is collected from baker.rules on every SceneView pass so edits made
    // in the inspector (add/remove/reorder) never leave stale references behind.
    private static MapPresentationBaker sceneBaker;
    private static readonly List<RouteDrawItem> sceneRouteItems = new List<RouteDrawItem>();
    private static readonly List<string> sceneRouteOptions = new List<string>();
    private static string[] sceneRouteOptionArray = Array.Empty<string>();
    private static readonly List<CandidateAnchorDrawItem> sceneAnchorItems = new List<CandidateAnchorDrawItem>();
    private static readonly List<string> sceneAnchorOptions = new List<string>();
    private static string[] sceneAnchorOptionArray = Array.Empty<string>();
    private static MapPresentationCandidate sceneSelectedCandidate;
    private static int sceneSelectedCandidateIndex = -1;
    private static int sceneSelectedAnchor = -1;
    private static MapPresentationRoute sceneSelectedRoute;
    private static int sceneSelectedRouteIndex = -1;
    private static int sceneSelectedWaypoint = -1;
    private static bool sceneShowRoutes = true;
    private static bool sceneShowAnchors;
    private static bool sceneSnapToUnitGrid;
    private static bool sceneArmAddPoint;
    private static bool sceneArmAddAnchor;
    private static bool sceneOverlayCollapsed;
    private static bool sceneAnchorMode;
    private static Rect sceneOverlayRect = new Rect(12f, 12f, 400f, 174f);
    private static GUIStyle scenePointLabelStyle;

    private sealed class RouteDrawItem
    {
        public MapPresentationRoute Route;
        public string DisplayName;
    }

    private sealed class CandidateAnchorDrawItem
    {
        public MapPresentationCandidate Candidate;
        public string DisplayName;
    }

    [InitializeOnLoadMethod]
    private static void WireSceneRouteEditing()
    {
        SceneView.duringSceneGui -= OnSceneViewRouteGUI;
        SceneView.duringSceneGui += OnSceneViewRouteGUI;
        Selection.selectionChanged -= SyncSceneBakerFromSelection;
        Selection.selectionChanged += SyncSceneBakerFromSelection;
    }

    [MenuItem("Tools/Map/Edit Presentation Routes (Draft Inspector)")]
    private static void OpenDraftRouteEditingWindow()
    {
        var baker = Selection.activeGameObject != null
            ? Selection.activeGameObject.GetComponent<MapPresentationBaker>()
            : null;
        if (baker == null)
        {
            EditorUtility.DisplayDialog(
                "Map Presentation Baker",
                "Select a GameObject with MapPresentationBaker in Hierarchy first.",
                "OK");
            return;
        }

        EditorWindow.GetWindow<BakerInspectorWindow>("Presentation Baker Draft").Bind(baker);
    }

    private sealed class BakerInspectorWindow : EditorWindow
    {
        private MapPresentationBaker baker;
        private Editor cachedEditor;

        public void Bind(MapPresentationBaker value)
        {
            if (cachedEditor != null) DestroyImmediate(cachedEditor);
            baker = value;
            cachedEditor = baker != null
                ? CreateEditor(baker, typeof(MapPresentationBakerEditor))
                : null;
            Repaint();
        }

        private void OnGUI()
        {
            if (baker == null || cachedEditor == null)
            {
                GUILayout.Label("No MapPresentationBaker selected.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            cachedEditor.OnInspectorGUI();
        }

        private void OnDestroy()
        {
            if (cachedEditor != null) DestroyImmediate(cachedEditor);
        }
    }

    private static void SyncSceneBakerFromSelection()
    {
        var gameObject = Selection.activeGameObject;
        sceneBaker = gameObject != null ? gameObject.GetComponent<MapPresentationBaker>() : null;
        if (sceneBaker != null) return;

        sceneSelectedRoute = null;
        sceneSelectedRouteIndex = -1;
    }

    private static void OnSceneViewRouteGUI(SceneView view)
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (sceneBaker == null) SyncSceneBakerFromSelection();
        if (sceneBaker == null) return;

        CollectSceneRoutes(sceneBaker);
        if (sceneRouteItems.Count == 0 && sceneAnchorItems.Count == 0) return;

        var selectedRoute = ResolveSelectedRoute();
        var selectedCandidate = ResolveSelectedCandidate();

        Handles.BeginGUI();
        DrawRouteOverlay();
        Handles.EndGUI();

        if (sceneAnchorMode)
        {
            if (selectedCandidate == null) return;

            DrawSelectedCandidateAnchors(selectedCandidate, sceneBaker.transform);
            ProcessArmAddAnchor(view, selectedCandidate, sceneBaker.transform);
            return;
        }

        if (!sceneShowRoutes || selectedRoute == null) return;

        DrawUnselectedRoutes(selectedRoute, sceneBaker.transform);
        DrawSelectedRoute(selectedRoute, sceneBaker.transform);
        DrawAnchorMarkers(sceneBaker);
        ProcessArmAddPoint(view, selectedRoute, sceneBaker.transform);
    }

    private static void CollectSceneRoutes(MapPresentationBaker baker)
    {
        sceneRouteItems.Clear();
        sceneRouteOptions.Clear();
        sceneAnchorItems.Clear();
        sceneAnchorOptions.Clear();
        if (baker == null || baker.rules == null) return;

        for (var ruleIndex = 0; ruleIndex < baker.rules.Length; ruleIndex++)
        {
            var rule = baker.rules[ruleIndex];
            if (rule == null) continue;

            AppendRouteItems(rule.behaviors, "Rule " + rule.ruleId);

            var candidates = rule.candidates;
            if (candidates == null) continue;

            for (var candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
            {
                var candidate = candidates[candidateIndex];
                if (candidate == null) continue;

                var assetName = GetCandidateAssetName(candidate.assetpath);
                var groupName = string.IsNullOrEmpty(assetName)
                    ? "Rule " + rule.ruleId + " / Candidate[" + candidateIndex + "]"
                    : "Rule " + rule.ruleId + " / " + assetName;
                AppendRouteItems(candidate.behaviors, groupName);

                if (CandidateHasRoute(candidate)) continue;
                sceneAnchorItems.Add(new CandidateAnchorDrawItem
                {
                    Candidate = candidate,
                    DisplayName = groupName
                });
                sceneAnchorOptions.Add(groupName);
            }
        }

        sceneRouteOptionArray = sceneRouteOptions.ToArray();
        sceneAnchorOptionArray = sceneAnchorOptions.ToArray();
    }

    private static void AppendRouteItems(MapPresentationBehavior[] behaviors, string groupName)
    {
        if (behaviors == null) return;

        for (var i = 0; i < behaviors.Length; i++)
        {
            if (!(behaviors[i] is MapPresentationRoute route)) continue;

            var item = new RouteDrawItem
            {
                Route = route,
                DisplayName = groupName + " / Route#" + sceneRouteItems.Count
            };
            sceneRouteItems.Add(item);
            sceneRouteOptions.Add(item.DisplayName);
        }
    }

    private static string GetCandidateAssetName(string assetpath)
    {
        if (string.IsNullOrEmpty(assetpath)) return string.Empty;

        return Path.GetFileNameWithoutExtension(assetpath.Replace('\\', '/'));
    }

    private static bool CandidateHasRoute(MapPresentationCandidate candidate)
    {
        var behaviors = candidate?.behaviors;
        if (behaviors == null) return false;

        for (var i = 0; i < behaviors.Length; i++)
        {
            if (behaviors[i] is MapPresentationRoute) return true;
        }

        return false;
    }

    private static MapPresentationRoute ResolveSelectedRoute()
    {
        for (var i = 0; i < sceneRouteItems.Count; i++)
        {
            if (!ReferenceEquals(sceneRouteItems[i].Route, sceneSelectedRoute)) continue;

            sceneSelectedRouteIndex = i;
            return sceneRouteItems[i].Route;
        }

        sceneSelectedRouteIndex = sceneRouteItems.Count > 0 ? 0 : -1;
        sceneSelectedRoute = sceneSelectedRouteIndex >= 0 ? sceneRouteItems[0].Route : null;
        sceneSelectedWaypoint = -1;
        return sceneSelectedRoute;
    }

    private static MapPresentationCandidate ResolveSelectedCandidate()
    {
        for (var i = 0; i < sceneAnchorItems.Count; i++)
        {
            if (!ReferenceEquals(sceneAnchorItems[i].Candidate, sceneSelectedCandidate)) continue;

            sceneSelectedCandidateIndex = i;
            return sceneAnchorItems[i].Candidate;
        }

        sceneSelectedCandidateIndex = sceneAnchorItems.Count > 0 ? 0 : -1;
        sceneSelectedCandidate = sceneSelectedCandidateIndex >= 0
            ? sceneAnchorItems[0].Candidate
            : null;
        sceneSelectedAnchor = -1;
        return sceneSelectedCandidate;
    }

    private static void DrawRouteOverlay()
    {
        sceneOverlayRect.height = sceneOverlayCollapsed ? 30f : 174f;
        GUI.Box(sceneOverlayRect, GUIContent.none, EditorStyles.helpBox);

        GUILayout.BeginArea(new Rect(
            sceneOverlayRect.x + 8f,
            sceneOverlayRect.y + 4f,
            sceneOverlayRect.width - 16f,
            sceneOverlayRect.height - 8f));

        GUILayout.BeginHorizontal();
        sceneOverlayCollapsed = GUILayout.Toggle(
            sceneOverlayCollapsed,
            sceneOverlayCollapsed ? "+" : "-",
            EditorStyles.miniButton,
            GUILayout.Width(20f));
        var title = sceneAnchorMode
            ? "Candidate Anchor Editing (" + sceneAnchorItems.Count + ")"
            : "Route Editing (" + sceneRouteItems.Count + ")";
        GUILayout.Label(title, EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        GUILayout.Label(sceneBaker != null ? sceneBaker.gameObject.name : "-", EditorStyles.miniLabel);
        GUILayout.EndHorizontal();

        if (sceneOverlayCollapsed)
        {
            GUILayout.EndArea();
            return;
        }

        var modeIndex = GUILayout.Toolbar(sceneAnchorMode ? 1 : 0, new[] { "Routes", "Anchors" }, EditorStyles.miniButton);
        if (modeIndex != (sceneAnchorMode ? 1 : 0))
        {
            sceneAnchorMode = modeIndex == 1;
            sceneArmAddPoint = false;
            sceneArmAddAnchor = false;
            sceneSelectedAnchor = -1;
        }

        if (!sceneAnchorMode)
        {
            if (sceneRouteOptionArray.Length == 0)
            {
                GUILayout.Label("No routes.", EditorStyles.miniLabel);
            }
            else
            {
                var popupIndex = Mathf.Clamp(sceneSelectedRouteIndex, 0, sceneRouteOptionArray.Length - 1);
                var newIndex = EditorGUILayout.Popup(popupIndex, sceneRouteOptionArray);
                if (newIndex != popupIndex)
                {
                    sceneSelectedRouteIndex = newIndex;
                    sceneSelectedRoute = sceneRouteItems[newIndex].Route;
                    sceneSelectedWaypoint = -1;
                }
            }

            GUILayout.BeginHorizontal();
            sceneArmAddPoint = GUILayout.Toggle(sceneArmAddPoint, "Add Waypoint", EditorStyles.miniButton);
            if (GUILayout.Button("- Point", EditorStyles.miniButton, GUILayout.Width(56f)))
                DeleteSelectedWaypoint(sceneSelectedRoute);

            if (GUILayout.Button("Focus", EditorStyles.miniButton, GUILayout.Width(48f)))
                FocusSelectedRoute();

            sceneShowRoutes = GUILayout.Toggle(sceneShowRoutes, "Show", EditorStyles.miniButton, GUILayout.Width(48f));
            GUILayout.EndHorizontal();
        }
        else
        {
            if (sceneAnchorOptionArray.Length == 0)
            {
                GUILayout.Label("No route-free candidates.", EditorStyles.miniLabel);
            }
            else
            {
                var popupIndex = Mathf.Clamp(sceneSelectedCandidateIndex, 0, sceneAnchorOptionArray.Length - 1);
                var newIndex = EditorGUILayout.Popup(popupIndex, sceneAnchorOptionArray);
                if (newIndex != popupIndex)
                {
                    sceneSelectedCandidateIndex = newIndex;
                    sceneSelectedCandidate = sceneAnchorItems[newIndex].Candidate;
                    sceneSelectedAnchor = -1;
                }
            }

            GUILayout.BeginHorizontal();
            sceneArmAddAnchor = GUILayout.Toggle(sceneArmAddAnchor, "Add Anchor", EditorStyles.miniButton);
            if (GUILayout.Button("- Anchor", EditorStyles.miniButton, GUILayout.Width(64f)))
                DeleteSelectedAnchor(sceneSelectedCandidate);

            if (GUILayout.Button("Focus", EditorStyles.miniButton, GUILayout.Width(48f)))
                FocusSelectedCandidate();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        GUILayout.BeginHorizontal();
        sceneSnapToUnitGrid = GUILayout.Toggle(sceneSnapToUnitGrid, "Snap 1 Unit", EditorStyles.miniButton, GUILayout.Width(84f));
        if (!sceneAnchorMode)
        {
            sceneShowAnchors = GUILayout.Toggle(sceneShowAnchors, "Anchors", EditorStyles.miniButton, GUILayout.Width(72f));
        }
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        GUILayout.Label(sceneAnchorMode
            ? "Drag: move | Click: select | Esc: exit Add Anchor"
            : "Drag: move | Click: select | Amber sphere: insert", EditorStyles.miniLabel);
        GUILayout.Label(sceneAnchorMode
            ? "Route candidates are hidden; Save via Generate And Save"
            : "Esc: exit Add Waypoint | Save via Generate And Save", EditorStyles.miniLabel);

        GUILayout.EndArea();
    }
    private static void DrawUnselectedRoutes(MapPresentationRoute selected, Transform root)
    {
        Handles.color = new Color(1f, 1f, 1f, 0.12f);
        for (var i = 0; i < sceneRouteItems.Count; i++)
        {
            var route = sceneRouteItems[i].Route;
            if (ReferenceEquals(route, selected)) continue;

            var points = route?.waypoints;
            if (points == null || points.Length < 2) continue;

            for (var p = 1; p < points.Length; p++)
                Handles.DrawLine(LocalToWorld(root, points[p - 1]), LocalToWorld(root, points[p]));

            if (route.loop)
                Handles.DrawLine(LocalToWorld(root, points[points.Length - 1]), LocalToWorld(root, points[0]));
        }
    }

    private static void DrawSelectedRoute(MapPresentationRoute route, Transform root)
    {
        var points = route?.waypoints;
        if (points == null || points.Length == 0) return;

        var evt = Event.current;
        var lineColor = new Color(0.2f, 0.85f, 1f, 0.95f);
        var worldPoints = new Vector3[points.Length];
        for (var i = 0; i < points.Length; i++)
            worldPoints[i] = LocalToWorld(root, points[i]);

        Handles.color = lineColor;
        for (var i = 1; i < worldPoints.Length; i++)
            Handles.DrawLine(worldPoints[i - 1], worldPoints[i]);

        if (route.loop && worldPoints.Length > 2)
            Handles.DrawLine(worldPoints[worldPoints.Length - 1], worldPoints[0]);

        for (var i = 0; i < worldPoints.Length - 1; i++)
            DrawInsertHandle(route, root, worldPoints[i], worldPoints[i + 1], i + 1);

        if (route.loop && worldPoints.Length > 2)
            DrawInsertHandle(route, root, worldPoints[worldPoints.Length - 1], worldPoints[0], worldPoints.Length);

        var labelStyle = GetScenePointLabelStyle();
        for (var i = 0; i < worldPoints.Length; i++)
        {
            var isSelected = sceneSelectedWaypoint == i;
            var size = Mathf.Max(
                HandleUtility.GetHandleSize(worldPoints[i]) * (isSelected ? 0.17f : 0.12f),
                0.08f);

            var controlId = GUIUtility.GetControlID(FocusType.Passive);
            if (evt.type == EventType.MouseDown && evt.button == 0 && HandleUtility.nearestControl == controlId)
            {
                sceneSelectedWaypoint = i;
                RecordSceneRouteUndo();
            }

            Handles.color = isSelected ? Color.yellow : lineColor;
            var fmh_1265_75_639270585216692387 = Quaternion.identity; var moved = Handles.FreeMoveHandle(controlId, worldPoints[i], size, Vector3.zero, Handles.SphereHandleCap);
            if ((moved - worldPoints[i]).sqrMagnitude > 0.0000001f)
            {
                var desiredWorld = sceneSnapToUnitGrid ? SnapWorldPosition(moved) : moved;
                route.waypoints[i] = root.InverseTransformPoint(desiredWorld);
                SetSceneBakerDirty();
            }

            Handles.Label(worldPoints[i] + Vector3.up * (size + 4f), " " + i, labelStyle);
        }
    }

    private static void DrawInsertHandle(
        MapPresentationRoute route,
        Transform root,
        Vector3 from,
        Vector3 to,
        int insertIndex)
    {
        var midWorld = (from + to) * 0.5f;
        var size = Mathf.Max(HandleUtility.GetHandleSize(midWorld) * 0.07f, 0.05f);
        var pickSize = Mathf.Max(size * 1.6f, size + 0.02f);

        Handles.color = new Color(1f, 0.78f, 0.2f, 0.85f);
        if (!Handles.Button(midWorld, Quaternion.identity, size, pickSize, Handles.SphereHandleCap)) return;

        var seg = to - from;
        var t = 0.5f;
        if (seg.sqrMagnitude > 0.0001f)
        {
            var ray = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);
            var plane = new Plane(Camera.current.transform.forward, root.position);
            if (plane.Raycast(ray, out var enter))
            {
                var clicked = ray.GetPoint(enter);
                t = Mathf.Clamp01(Vector3.Dot(clicked - from, seg) / seg.sqrMagnitude);
            }
        }

        var world = Vector3.Lerp(from, to, t);
        if (sceneSnapToUnitGrid) world = SnapWorldPosition(world);

        InsertWaypointAt(route, root.InverseTransformPoint(world), insertIndex);
    }

    private static void InsertWaypointAt(MapPresentationRoute route, Vector3 localPoint, int index)
    {
        RecordSceneRouteUndo();

        var points = route.waypoints ?? Array.Empty<Vector3>();
        var next = new Vector3[points.Length + 1];
        var offset = 0;
        for (var i = 0; i < next.Length; i++)
        {
            if (i == index)
            {
                next[i] = localPoint;
                offset = 1;
                continue;
            }

            next[i] = points[i - offset];
        }

        route.waypoints = next;
        sceneSelectedWaypoint = index;
        SetSceneBakerDirty();
        HandleUtility.Repaint();
    }

    private static void DeleteSelectedWaypoint(MapPresentationRoute route)
    {
        if (route?.waypoints == null || route.waypoints.Length == 0) return;

        var index = sceneSelectedWaypoint >= 0 && sceneSelectedWaypoint < route.waypoints.Length
            ? sceneSelectedWaypoint
            : route.waypoints.Length - 1;

        var next = new Vector3[route.waypoints.Length - 1];
        var offset = 0;
        for (var i = 0; i < next.Length + 1; i++)
        {
            if (i == index)
            {
                offset = 1;
                continue;
            }

            next[i - offset] = route.waypoints[i];
        }

        RecordSceneRouteUndo();
        route.waypoints = next;
        sceneSelectedWaypoint = -1;
        SetSceneBakerDirty();
        HandleUtility.Repaint();
    }

    private static void DrawAnchorMarkers(MapPresentationBaker baker)
    {
        if (!sceneShowAnchors || baker == null || baker.rules == null) return;

        Handles.color = new Color(1f, 1f, 1f, 0.2f);
        foreach (var rule in baker.rules)
        {
            if (rule?.candidates == null) continue;

            foreach (var candidate in rule.candidates)
            {
                if (candidate == null) continue;

                var anchors = ResolveSceneCandidateAnchors(candidate);
                if (anchors == null) continue;

                foreach (var anchor in anchors)
                {
                    var world = baker.transform.TransformPoint(anchor);
                    Handles.DotHandleCap(
                        0,
                        world,
                        Quaternion.identity,
                        Mathf.Max(HandleUtility.GetHandleSize(world) * 0.08f, 0.05f),
                        EventType.Repaint);
                }
            }
        }
    }

    private static void DrawSelectedCandidateAnchors(MapPresentationCandidate candidate, Transform root)
    {
        var points = candidate?.anchorPoints;
        if (points == null || points.Length == 0) return;

        var evt = Event.current;
        var pointColor = new Color(1f, 0.62f, 0.15f, 0.95f);
        var labelStyle = GetScenePointLabelStyle();
        for (var i = 0; i < points.Length; i++)
        {
            var world = LocalToWorld(root, points[i]);
            var isSelected = sceneSelectedAnchor == i;
            var size = Mathf.Max(
                HandleUtility.GetHandleSize(world) * (isSelected ? 0.17f : 0.12f),
                0.08f);

            var controlId = GUIUtility.GetControlID(FocusType.Passive);
            if (evt.type == EventType.MouseDown && evt.button == 0 && HandleUtility.nearestControl == controlId)
            {
                sceneSelectedAnchor = i;
                RecordSceneRouteUndo();
            }

            Handles.color = isSelected ? Color.yellow : pointColor;
            var moved = Handles.FreeMoveHandle(controlId, world, size, Vector3.zero, Handles.SphereHandleCap);
            if ((moved - world).sqrMagnitude > 0.0000001f)
            {
                var desiredWorld = sceneSnapToUnitGrid ? SnapWorldPosition(moved) : moved;
                candidate.anchorPoints[i] = root != null
                    ? root.InverseTransformPoint(desiredWorld)
                    : desiredWorld;
                SetSceneBakerDirty();
            }

            Handles.Label(world + Vector3.up * (size + 4f), " " + i, labelStyle);
        }
    }

    private static void ProcessArmAddAnchor(SceneView view, MapPresentationCandidate candidate, Transform root)
    {
        if (!sceneArmAddAnchor || candidate == null) return;

        var evt = Event.current;
        if (evt.type == EventType.KeyDown && evt.keyCode == KeyCode.Escape)
        {
            sceneArmAddAnchor = false;
            evt.Use();
            return;
        }

        if (evt.type != EventType.MouseDown || evt.button != 0) return;
        if (sceneOverlayRect.Contains(evt.mousePosition)) return;

        var ray = HandleUtility.GUIPointToWorldRay(evt.mousePosition);
        var camera = view.camera;
        if (camera == null) return;

        var plane = new Plane(camera.transform.forward, root != null ? root.position : Vector3.zero);
        if (!plane.Raycast(ray, out var enter)) return;

        var world = SnapWorldPosition(ray.GetPoint(enter));
        AppendCandidateAnchor(candidate, root != null ? root.InverseTransformPoint(world) : world);
        HandleUtility.Repaint();
        evt.Use();
    }

    private static void AppendCandidateAnchor(MapPresentationCandidate candidate, Vector3 localPoint)
    {
        if (candidate == null) return;

        RecordSceneRouteUndo();

        var points = candidate.anchorPoints ?? Array.Empty<Vector3>();
        var next = new Vector3[points.Length + 1];
        points.CopyTo(next, 0);
        next[next.Length - 1] = localPoint;
        candidate.anchorPoints = next;
        sceneSelectedAnchor = next.Length - 1;
        SetSceneBakerDirty();
    }

    private static void DeleteSelectedAnchor(MapPresentationCandidate candidate)
    {
        if (candidate?.anchorPoints == null || candidate.anchorPoints.Length == 0) return;

        var index = sceneSelectedAnchor >= 0 && sceneSelectedAnchor < candidate.anchorPoints.Length
            ? sceneSelectedAnchor
            : candidate.anchorPoints.Length - 1;

        var next = new Vector3[candidate.anchorPoints.Length - 1];
        var offset = 0;
        for (var i = 0; i < next.Length + 1; i++)
        {
            if (i == index)
            {
                offset = 1;
                continue;
            }

            next[i - offset] = candidate.anchorPoints[i];
        }

        RecordSceneRouteUndo();
        candidate.anchorPoints = next;
        sceneSelectedAnchor = -1;
        SetSceneBakerDirty();
        HandleUtility.Repaint();
    }

    private static void FocusSelectedCandidate()
    {
        var candidate = ResolveSelectedCandidate();
        var view = SceneView.lastActiveSceneView;
        var points = candidate?.anchorPoints;
        if (points == null || points.Length == 0 || view == null) return;

        var root = sceneBaker != null ? sceneBaker.transform : null;
        var bounds = new Bounds(LocalToWorld(root, points[0]), Vector3.zero);
        for (var i = 1; i < points.Length; i++)
            bounds.Encapsulate(LocalToWorld(root, points[i]));

        if (bounds.size.sqrMagnitude < 0.01f)
            bounds.Expand(4f);

        view.LookAt(bounds.center, view.rotation);
        view.size = Mathf.Max(bounds.extents.magnitude, 3f) * 1.6f;
        view.Repaint();
    }
    private static Vector3[] ResolveSceneCandidateAnchors(MapPresentationCandidate candidate)
    {
        if (candidate.anchorPoints is { Length: > 0 })
            return candidate.anchorPoints;

        var behaviors = candidate.behaviors;
        if (behaviors == null) return null;

        for (var i = 0; i < behaviors.Length; i++)
        {
            if (behaviors[i] is not MapPresentationRoute route) continue;
            if (route.waypoints is { Length: > 0 })
                return new[] { route.waypoints[0] };
        }

        return null;
    }

    private static void ProcessArmAddPoint(SceneView view, MapPresentationRoute route, Transform root)
    {
        if (!sceneArmAddPoint) return;

        var evt = Event.current;
        if (evt.type == EventType.KeyDown && evt.keyCode == KeyCode.Escape)
        {
            sceneArmAddPoint = false;
            evt.Use();
            return;
        }

        if (evt.type != EventType.MouseDown || evt.button != 0) return;
        if (sceneOverlayRect.Contains(evt.mousePosition)) return;

        var ray = HandleUtility.GUIPointToWorldRay(evt.mousePosition);
        var plane = new Plane(view.camera.transform.forward, root.position);
        if (!plane.Raycast(ray, out var enter)) return;

        var world = SnapWorldPosition(ray.GetPoint(enter));
        AppendWaypoint(route, root.InverseTransformPoint(world));
        HandleUtility.Repaint();
        evt.Use();
    }

    private static void AppendWaypoint(MapPresentationRoute route, Vector3 localPoint)
    {
        RecordSceneRouteUndo();

        var points = route.waypoints ?? Array.Empty<Vector3>();
        var next = new Vector3[points.Length + 1];
        points.CopyTo(next, 0);
        next[next.Length - 1] = localPoint;
        route.waypoints = next;
        sceneSelectedWaypoint = next.Length - 1;
        SetSceneBakerDirty();
    }

    private static void FocusSelectedRoute()
    {
        var route = ResolveSelectedRoute();
        var view = SceneView.lastActiveSceneView;
        var points = route?.waypoints;
        if (points == null || points.Length == 0 || view == null) return;

        var root = sceneBaker != null ? sceneBaker.transform : null;
        var bounds = new Bounds(LocalToWorld(root, points[0]), Vector3.zero);
        for (var i = 1; i < points.Length; i++)
            bounds.Encapsulate(LocalToWorld(root, points[i]));

        if (bounds.size.sqrMagnitude < 0.01f)
            bounds.Expand(4f);

        view.LookAt(bounds.center, view.rotation);
        view.size = Mathf.Max(bounds.extents.magnitude, 3f) * 1.6f;
        view.Repaint();
    }

    private static Vector3 LocalToWorld(Transform root, Vector3 localPoint)
    {
        return root != null ? root.TransformPoint(localPoint) : localPoint;
    }

    private static Vector3 SnapWorldPosition(Vector3 position)
    {
        if (!sceneSnapToUnitGrid) return position;

        return new Vector3(
            Mathf.Round(position.x),
            Mathf.Round(position.y),
            Mathf.Round(position.z));
    }

    private static void RecordSceneRouteUndo()
    {
        if (sceneBaker == null) return;

        Undo.RegisterCompleteObjectUndo(sceneBaker, EditorUndoPrefix + " Scene Route Edit");
    }

    private static void SetSceneBakerDirty()
    {
        if (sceneBaker == null) return;

        EditorUtility.SetDirty(sceneBaker);
    }

    private static GUIStyle GetScenePointLabelStyle()
    {
        if (scenePointLabelStyle != null) return scenePointLabelStyle;

        scenePointLabelStyle = new GUIStyle(EditorStyles.whiteMiniLabel)
        {
            alignment = TextAnchor.MiddleLeft,
            fontSize = 9
        };
        return scenePointLabelStyle;
    }

    #endregion

}
#endif