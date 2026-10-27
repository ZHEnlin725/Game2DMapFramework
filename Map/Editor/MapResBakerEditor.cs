#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.UIElements;

[CustomEditor(typeof(MapResBaker))]
public sealed class MapResBakerEditor : Editor
{
    private const string EditorUndoPrefix = "[MapResBakerEditor]";

    private void OnEnable()
    {
        FillMapIdFromDataFileName();
    }

    public override VisualElement CreateInspectorGUI()
    {
        var root = new VisualElement();
        root.Add(CreateHelpBox());
        root.Add(CreateSection("公共字段", CreatePublicFieldSection()));
        root.Add(CreateSection("序列化字段", CreateSerializeFieldSection()));
        root.Add(CreateSection("公共方法", CreateMethodSection()));
        return root;
    }

    private VisualElement CreateHelpBox()
    {
        var helpBox = new HelpBox(
            "数组列表支持点击选中、删除选中项、拖拽排序以及关键字过滤。",
            HelpBoxMessageType.Info);
        helpBox.style.marginBottom = 4f;
        return helpBox;
    }

    private VisualElement CreatePublicFieldSection()
    {
        var container = new VisualElement();
        container.Add(CreateBoundProperty("mapId", "Map Id"));
        container.Add(CreateBoundProperty("filename", "Filename"));
        container.Add(CreateBoundProperty("storagepath", "Storage Path"));
        container.Add(CreateBoundProperty("data", "Data"));
        return container;
    }

    private VisualElement CreateSerializeFieldSection()
    {
        var container = new VisualElement();

        container.Add(CreateSearchableSerializedArray(
            "resources",
            "Resources",
            DescribeObjectReference,
            DrawObjectReferenceElement,
            GetObjectReferenceHeight,
            true));

        container.Add(CreateSearchableSerializedArray(
            "zwriteObjects",
            "ZWrite Objects",
            DescribeObjectReference,
            DrawObjectReferenceElement,
            GetObjectReferenceHeight));

        container.Add(CreateSearchableSerializedArray(
            "reslist.resources",
            "Res Entries",
            DescribeResEntry,
            DrawResEntry,
            GetResEntryHeight));

        return container;
    }
    private VisualElement CreateMethodSection()
    {
        var container = new VisualElement();
        container.style.flexDirection = FlexDirection.Row;
        container.Add(CreateMethodButton("Modify", target =>
        {
            if (!TryConfirmOverwriteBytes(string.Format(MapDefine.ResDataFormat, target.mapId)))
                return;

            target.Modify();
            serializedObject.ApplyModifiedProperties();
        }));
        container.Add(CreateMethodButton("Deserialize", target =>
        {
            target.Deserialize();
            serializedObject.ApplyModifiedProperties();
        }));
        return container;
    }

    private VisualElement CreateMethodButton(string label, Action<MapResBaker> execute)
    {
        var button = new Button(() =>
        {
            if (target is not MapResBaker baker)
                return;

            Undo.RecordObject(baker, EditorUndoPrefix + " " + label);
            execute(baker);
            EditorUtility.SetDirty(baker);
        })
        {
            text = label
        };

        button.style.flexGrow = 1f;
        return button;
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

    private PropertyField CreateBoundProperty(string fieldName, string label)
    {
        var property = serializedObject.FindProperty(fieldName);
        var field = new PropertyField(property, label)
        {
            style =
            {
                marginTop = 2f,
                marginBottom = 2f
            }
        };
        field.Bind(serializedObject);
        if (fieldName == "data")
            field.RegisterCallback<UnityEngine.UIElements.ChangeEvent<SerializedProperty>>(_ => FillMapIdFromDataFileName());
        return field;
    }

    private void FillMapIdFromDataFileName()
    {
        if (target is not MapResBaker baker || baker.data == null) return;

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

    private static VisualElement CreateSection(string title, VisualElement content)
    {
        var foldout = new Foldout
        {
            text = title,
            value = true
        };
        foldout.contentContainer.Add(content);
        return foldout;
    }

    private VisualElement CreateSearchableSerializedArray(
        string propertyName,
        string displayName,
        Func<SerializedProperty, string> describe,
        Action<Rect, SerializedProperty, int> drawElement,
        Func<SerializedProperty, float> elementHeight,
        bool allowGameObjectDrop = false)
    {
        var container = new VisualElement();
        container.style.marginBottom = 10f;

        var title = new Label(displayName);
        title.style.unityFontStyleAndWeight = FontStyle.Bold;
        title.style.marginBottom = 4f;
        container.Add(title);

        var view = new ArrayView
        {
            FieldName = propertyName,
            DisplayName = displayName,
            Describe = describe,
            DrawElement = drawElement,
            ElementHeight = elementHeight,
            Search = string.Empty
        };

        if (allowGameObjectDrop)
        {
            var dropContainer = new IMGUIContainer(() => DrawGameObjectDropTarget(view));
            dropContainer.style.marginBottom = 6f;
            container.Add(dropContainer);
        }

        var arrayContainer = new IMGUIContainer(() => DrawSearchableSerializedArray(view));

        var searchField = new ToolbarSearchField
        {
            value = view.Search
        };

        searchField.RegisterValueChangedCallback(evt =>
        {
            view.Search = evt.newValue;
            arrayContainer.MarkDirtyRepaint();
        });

        searchField.style.marginBottom = 6f;
        container.Add(searchField);
        container.Add(arrayContainer);
        return container;
    }

    private void DrawGameObjectDropTarget(ArrayView view)
    {
        var property = ResolveArrayProperty(view.FieldName);
        if (property == null || !property.isArray)
            return;

        const float dropHeight = 28f;
        var rect = GUILayoutUtility.GetRect(1f, dropHeight, GUILayout.ExpandWidth(true));
        var dragInside = rect.Contains(Event.current.mousePosition);

        if (Event.current.type == EventType.DragUpdated && dragInside)
        {
            DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
            Event.current.Use();
        }
        else if (Event.current.type == EventType.DragPerform && dragInside)
        {
            DragAndDrop.AcceptDrag();
            HandleGameObjectDrop(property);
            Event.current.Use();
        }

        if (Event.current.type == EventType.Repaint)
        {
            var hintText = dragInside
                ? "松开以复制选中的场景 GameObject"
                : "拖拽场景 GameObject 到此处（可多选，自动去重）";
            GUI.Box(rect, hintText, EditorStyles.helpBox);
        }
    }

    private void HandleGameObjectDrop(SerializedProperty resources)
    {
        var usedIds = new HashSet<int>();
        for (var i = 0; i < resources.arraySize; i++)
        {
            var reference = resources.GetArrayElementAtIndex(i).objectReferenceValue;
            if (reference != null)
                usedIds.Add(reference.GetInstanceID());
        }

        var addedCount = 0;
        foreach (var dropped in DragAndDrop.objectReferences)
        {
            if (dropped is not GameObject gameObject || !gameObject.scene.IsValid())
                continue;

            var instanceId = gameObject.GetInstanceID();
            if (!usedIds.Add(instanceId))
                continue;

            Undo.RecordObject(target, EditorUndoPrefix + " Add Resources From Drag");
            var index = resources.arraySize;
            resources.InsertArrayElementAtIndex(index);
            resources.GetArrayElementAtIndex(index).objectReferenceValue = gameObject;
            addedCount++;
        }

        if (addedCount > 0)
        {
            serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(target);
            Repaint();
        }
    }
    private void DrawSearchableSerializedArray(ArrayView view)
    {
        var property = ResolveArrayProperty(view.FieldName);
        if (property == null)
        {
            EditorGUILayout.HelpBox(view.FieldName + ": no serialized property", MessageType.Info);
            return;
        }

        if (!property.isArray)
        {
            EditorGUILayout.HelpBox(view.FieldName + ": not an array property", MessageType.Warning);
            return;
        }

        serializedObject.Update();

        var stateKey = "array:" + view.FieldName;
        var state = GetOrCreateListState(stateKey);
        RefreshVisibleIndices(state, property, view.Describe, view.Search);

        var reorderableList = CreateReorderableList(property, state, view);
        reorderableList.DoLayoutList();

        serializedObject.ApplyModifiedProperties();
    }

    private ReorderableList CreateReorderableList(
        SerializedProperty arrayProperty,
        EditableListState state,
        ArrayView view)
    {
        if (view.List != null && view.CachedSearch == view.Search)
            return view.List;

        view.CachedSearch = view.Search;
        var canReorder = string.IsNullOrWhiteSpace(view.Search);
        var previousOrder = new List<int>(state.VisibleIndices);

        var reorderableList = canReorder
            ? new ReorderableList(
                serializedObject,
                arrayProperty,
                true,
                false,
                true,
                true)
            : new ReorderableList(
                state.VisibleIndices,
                typeof(int),
                false,
                false,
                true,
                true);

        view.List = reorderableList;

        if (state.SelectedRow >= 0 && state.SelectedRow < arrayProperty.arraySize)
            reorderableList.index = state.SelectedRow;

        reorderableList.drawElementCallback = (rect, rowIndex, isActive, _) =>
        {
            if (rowIndex < 0 || rowIndex >= state.VisibleIndices.Count)
                return;

            var previousSelectedRow = state.SelectedRow;
            if (isActive)
            {
                state.SelectedRow = rowIndex;
                reorderableList.index = rowIndex;
            }

            TrackRowSelection(state, rect, rowIndex);
            if (state.SelectedRow != previousSelectedRow)
            {
                reorderableList.index = rowIndex;
            }

            DrawSelectionBackground(state, rect, rowIndex);

            var actualIndex = state.VisibleIndices[rowIndex];
            var element = arrayProperty.GetArrayElementAtIndex(actualIndex);

            if (view.DrawElement != null)
            {
                view.DrawElement(rect, element, actualIndex);
                return;
            }

            EditorGUI.PropertyField(rect, element, GUIContent.none, true);
        };

        reorderableList.elementHeightCallback = rowIndex =>
        {
            if (rowIndex < 0 || rowIndex >= state.VisibleIndices.Count)
                return EditorGUIUtility.singleLineHeight;

            var element = arrayProperty.GetArrayElementAtIndex(state.VisibleIndices[rowIndex]);
            if (view.ElementHeight != null)
                return Mathf.Max(EditorGUIUtility.singleLineHeight, view.ElementHeight(element));

            return Mathf.Max(EditorGUIUtility.singleLineHeight, EditorGUI.GetPropertyHeight(element, true));
        };

        reorderableList.onAddCallback = _ =>
        {
            Undo.RecordObject(target, EditorUndoPrefix + " Add " + view.FieldName);
            arrayProperty.InsertArrayElementAtIndex(arrayProperty.arraySize);
            state.SelectedRow = state.VisibleIndices.Count;
            EditorUtility.SetDirty(target);
        };

        reorderableList.onRemoveCallback = list =>
        {
            if (state.SelectedRow < 0 && list.index >= 0 &&
                list.index < state.VisibleIndices.Count)
            {
                state.SelectedRow = list.index;
            }

            RemoveSelectedRow(arrayProperty, state);
        };

        if (!canReorder)
        {
            reorderableList.onReorderCallback = _ =>
            {
                if (!CommitReorder(previousOrder, state.VisibleIndices, arrayProperty))
                    return;

                FollowSelectionAfterReorder(previousOrder, state);
                reorderableList.index = state.SelectedRow;
            };
        }

        return reorderableList;
    }

    private static void FollowSelectionAfterReorder(
        List<int> previousOrder,
        EditableListState state)
    {
        if (state.SelectedRow < 0 || state.SelectedRow >= previousOrder.Count)
            return;

        var previousActualIndex = previousOrder[state.SelectedRow];
        state.SelectedRow = state.VisibleIndices.IndexOf(previousActualIndex);
    }

    private void RemoveSelectedRow(SerializedProperty arrayProperty, EditableListState state)
    {
        if (state.SelectedRow < 0 || state.SelectedRow >= state.VisibleIndices.Count)
            return;

        Undo.RecordObject(target, EditorUndoPrefix + " Remove Selected Item");
        arrayProperty.DeleteArrayElementAtIndex(state.VisibleIndices[state.SelectedRow]);
        state.SelectedRow = -1;
        EditorUtility.SetDirty(target);
    }

    private bool CommitReorder(List<int> previousOrder, List<int> currentOrder, SerializedProperty arrayProperty)
    {
        if (previousOrder.Count == 0 || previousOrder.Count != currentOrder.Count)
            return false;

        if (currentOrder.Count != arrayProperty.arraySize)
            return false;

        var logicalPositions = new List<int>(previousOrder);
        Undo.RecordObject(target, EditorUndoPrefix + " Reorder");

        for (var i = 0; i < currentOrder.Count; i++)
        {
            var sourceSlot = logicalPositions.IndexOf(currentOrder[i]);
            if (sourceSlot < 0)
                return false;

            if (sourceSlot != i)
                arrayProperty.MoveArrayElement(sourceSlot, i);

            ShiftLogicalPosition(logicalPositions, sourceSlot, i);
        }

        EditorUtility.SetDirty(target);
        return true;
    }

    private static void ShiftLogicalPosition(List<int> positions, int from, int to)
    {
        if (from == to)
            return;

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

    private SerializedProperty ResolveArrayProperty(string propertyName)
    {
        var parts = propertyName.Split('.');
        SerializedProperty property = null;

        for (var i = 0; i < parts.Length; i++)
        {
            property = i == 0
                ? serializedObject.FindProperty(parts[0])
                : property?.FindPropertyRelative(parts[i]);

            if (property == null)
                return null;
        }

        return property;
    }

    private static void RefreshVisibleIndices(
        EditableListState state,
        SerializedProperty arrayProperty,
        Func<SerializedProperty, string> describe,
        string keyword)
    {
        state.VisibleIndices.Clear();

        for (var i = 0; i < arrayProperty.arraySize; i++)
        {
            var element = arrayProperty.GetArrayElementAtIndex(i);
            if (MatchesKeyword(describe(element), keyword))
                state.VisibleIndices.Add(i);
        }

        if (state.SelectedRow >= state.VisibleIndices.Count)
            state.SelectedRow = state.VisibleIndices.Count - 1;
    }

    private static bool MatchesKeyword(string text, string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return true;

        return text.IndexOf(keyword.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
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

        var color = new Color(0.24f, 0.47f, 0.95f, 0.18f);
        EditorGUI.DrawRect(new Rect(rowRect.x, rowRect.y, rowRect.width, rowRect.height), color);
    }

    private static void DrawObjectReferenceElement(Rect rect, SerializedProperty element, int _)
    {
        EditorGUI.PropertyField(rect, element, GUIContent.none, true);
    }

    private static float GetObjectReferenceHeight(SerializedProperty element)
    {
        return EditorGUI.GetPropertyHeight(element, true);
    }

    private static string DescribeObjectReference(SerializedProperty property)
    {
        var reference = property.objectReferenceValue;
        return reference == null ? string.Empty : reference.name;
    }

    private static string DescribeResEntry(SerializedProperty property)
    {
        var assetpath = property.FindPropertyRelative("assetpath")?.stringValue ?? string.Empty;
        var resTag = property.FindPropertyRelative("resTag")?.intValue ?? 0;
        return resTag + ";" + assetpath;
    }

    private void DrawResEntry(Rect rect, SerializedProperty entry, int index)
    {
        var x = rect.x;
        var y = rect.y;
        var width = rect.width;
        var lineHeight = EditorGUIUtility.singleLineHeight;
        var spacing = EditorGUIUtility.standardVerticalSpacing;

        var assetpath = entry.FindPropertyRelative("assetpath")?.stringValue ?? string.Empty;
        var header = string.IsNullOrEmpty(assetpath) ? "<no assetpath>" : assetpath;

        entry.isExpanded = EditorGUI.Foldout(
            new Rect(x, y + 2f, width, lineHeight),
            entry.isExpanded,
            "[" + index + "] " + header,
            true);

        y += lineHeight + spacing;
        if (!entry.isExpanded)
            return;

        DrawPropertyRow(entry.FindPropertyRelative("resTag"), x, ref y, width);
        DrawPropertyRow(entry.FindPropertyRelative("assetpath"), x, ref y, width);
        DrawPropertyRow(entry.FindPropertyRelative("localBounds"), x, ref y, width);
        DrawPropertyRow(entry.FindPropertyRelative("localPosition"), x, ref y, width);
        DrawPropertyRow(entry.FindPropertyRelative("localScale"), x, ref y, width);
        DrawPropertyRow(entry.FindPropertyRelative("localEulerAngles"), x, ref y, width);

        DrawBehaviorEditor(x, ref y, width, entry.FindPropertyRelative("behaviors"));
    }

    private float GetResEntryHeight(SerializedProperty entry)
    {
        var lineHeight = EditorGUIUtility.singleLineHeight;
        var spacing = EditorGUIUtility.standardVerticalSpacing;

        var height = lineHeight + spacing;
        if (!entry.isExpanded)
            return height;

        height += GetPropertyRowHeight(entry.FindPropertyRelative("resTag")) + spacing;
        height += GetPropertyRowHeight(entry.FindPropertyRelative("assetpath")) + spacing;
        height += GetPropertyRowHeight(entry.FindPropertyRelative("localBounds")) + spacing;
        height += GetPropertyRowHeight(entry.FindPropertyRelative("localPosition")) + spacing;
        height += GetPropertyRowHeight(entry.FindPropertyRelative("localScale")) + spacing;
        height += GetPropertyRowHeight(entry.FindPropertyRelative("localEulerAngles")) + spacing;

        var behaviors = entry.FindPropertyRelative("behaviors");
        if (behaviors != null)
        {
            height += GetBehaviorEditorHeight(behaviors, lineHeight, spacing);
            height += 8f;
        }

        return height;
    }

    private static float GetPropertyRowHeight(SerializedProperty property)
    {
        return property == null
            ? EditorGUIUtility.singleLineHeight
            : EditorGUI.GetPropertyHeight(property, true);
    }

    private void DrawPropertyRow(SerializedProperty property, float x, ref float y, float width)
    {
        if (property == null)
            return;

        var height = EditorGUI.GetPropertyHeight(property, true);
        EditorGUI.PropertyField(new Rect(x, y, width, height), property, true);
        y += height + EditorGUIUtility.standardVerticalSpacing;
    }

    private void DrawBehaviorEditor(float x, ref float y, float width, SerializedProperty behaviors)
    {
        if (behaviors == null)
            return;

        if (behaviors.arraySize == 0)
        {
            var addButtonRect = new Rect(x, y, width, EditorGUIUtility.singleLineHeight);
            if (GUI.Button(addButtonRect, "Add", EditorStyles.miniButton))
                ShowBehaviorTypeMenu(addButtonRect, behaviors);

            y += EditorGUIUtility.singleLineHeight + EditorGUIUtility.standardVerticalSpacing;
            return;
        }

        var lineHeight = EditorGUIUtility.singleLineHeight;
        var spacing = EditorGUIUtility.standardVerticalSpacing;

        if (!_behaviorSearchKeywords.TryGetValue(behaviors.propertyPath, out var searchKeyword))
            searchKeyword = string.Empty;

        var searchLabelWidth = 40f;
        EditorGUI.LabelField(new Rect(x, y, searchLabelWidth, lineHeight), "搜索");
        searchKeyword = EditorGUI.TextField(
            new Rect(x + searchLabelWidth, y, width - searchLabelWidth, lineHeight),
            searchKeyword);
        _behaviorSearchKeywords[behaviors.propertyPath] = searchKeyword;

        y += lineHeight + spacing;

        var stateKey = "behavior:" + behaviors.propertyPath;
        var state = GetOrCreateListState(stateKey);
        RefreshVisibleIndices(state, behaviors, DescribeBehavior, searchKeyword);

        EditorGUI.LabelField(
            new Rect(x, y, width, lineHeight),
            "Behaviors (" + state.VisibleIndices.Count + "/" + behaviors.arraySize + ")");
        y += lineHeight + spacing;

        var listHeight = GetBehaviorReorderableListHeight(state, behaviors, lineHeight, spacing);
        var previousOrder = new List<int>(state.VisibleIndices);
        var canReorder = string.IsNullOrWhiteSpace(searchKeyword);

        var reorderableList = new ReorderableList(
            state.VisibleIndices,
            typeof(int),
            canReorder,
            false,
            true,
            true);

        reorderableList.drawElementCallback = (rect, rowIndex, isActive, _) =>
        {
            if (rowIndex < 0 || rowIndex >= state.VisibleIndices.Count)
                return;

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

        reorderableList.onAddDropdownCallback = (buttonRect, _) =>
        {
            ShowBehaviorTypeMenu(buttonRect, behaviors);
        };

        reorderableList.onRemoveCallback = list =>
        {
            if (state.SelectedRow < 0 && list.index >= 0 &&
                list.index < state.VisibleIndices.Count)
            {
                state.SelectedRow = list.index;
            }

            if (state.SelectedRow < 0 || state.SelectedRow >= state.VisibleIndices.Count)
                return;

            Undo.RecordObject(target, EditorUndoPrefix + " Remove Behavior");
            behaviors.DeleteArrayElementAtIndex(state.VisibleIndices[state.SelectedRow]);
            serializedObject.ApplyModifiedProperties();
            Repaint();
            state.SelectedRow = -1;
            EditorUtility.SetDirty(target);
            GUIUtility.ExitGUI();
        };

        reorderableList.onReorderCallback = _ =>
        {
            CommitReorder(previousOrder, state.VisibleIndices, behaviors);
        };

        reorderableList.DoList(new Rect(x, y, width, listHeight));
        y += listHeight + spacing;
    }

    private float GetBehaviorEditorHeight(SerializedProperty behaviors, float lineHeight, float spacing)
    {
        if (behaviors == null)
            return lineHeight;

        if (behaviors.arraySize == 0)
            return lineHeight + spacing;

        var stateKey = "behavior:" + behaviors.propertyPath;
        var state = GetOrCreateListState(stateKey);

        _behaviorSearchKeywords.TryGetValue(behaviors.propertyPath, out var searchKeyword);
        RefreshVisibleIndices(state, behaviors, DescribeBehavior, searchKeyword);

        var height = lineHeight + spacing; // search row
        height += lineHeight + spacing;    // header row
        height += GetBehaviorReorderableListHeight(state, behaviors, lineHeight, spacing);
        return height;
    }

    private static float GetBehaviorReorderableListHeight(
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

        // ReorderableList 自带的 +/- 按钮区；空列表时额外留出底部间距，避免和外层列表按钮重叠。
        height += EditorGUIUtility.singleLineHeight + 6f;
        if (behaviors.arraySize == 0) height += 20f;

        return Mathf.Max(lineHeight, height);
    }

    private static void DrawBehaviorRow(Rect rect, SerializedProperty behavior)
    {
        var lineHeight = EditorGUIUtility.singleLineHeight;
        var spacing = EditorGUIUtility.standardVerticalSpacing;

        var instance = ResolveBehaviorValue(behavior) as MapResBehavior;
        var label = instance != null ? instance.DisplayName : "null";

        behavior.isExpanded = EditorGUI.Foldout(
            new Rect(rect.x, rect.y, rect.width, lineHeight),
            behavior.isExpanded,
            label,
            true);

        if (!behavior.isExpanded)
            return;

        var y = rect.y + lineHeight + spacing;
        EditorGUI.indentLevel++;

        var inst = behavior.Copy();
        foreach (SerializedProperty child in behavior)
        {
            if (!ShouldShowBehaviorChild(inst, child))
                continue;

            var height = EditorGUI.GetPropertyHeight(child, true);
            if (child.name == "eventKey")
            {
                DrawEventKeyDropdown(new Rect(rect.x, y, rect.width, lineHeight), behavior, child);
            }
            else
            {
                EditorGUI.PropertyField(new Rect(rect.x, y, rect.width, height), child, true);
            }

            y += height + spacing;
        }

        EditorGUI.indentLevel--;
    }

    private static float GetBehaviorRowHeight(SerializedProperty behavior)
    {
        var lineHeight = EditorGUIUtility.singleLineHeight;
        var spacing = EditorGUIUtility.standardVerticalSpacing;

        if (!behavior.isExpanded)
            return lineHeight + 2f;

        var height = lineHeight + spacing;
        var inst = behavior.Copy();
        foreach (SerializedProperty child in behavior)
        {
            if (!ShouldShowBehaviorChild(inst, child))
                continue;

            height += EditorGUI.GetPropertyHeight(child, true) + spacing;
        }

        return height;
    }

    private static void DrawEventKeyDropdown(Rect rect, SerializedProperty behavior, SerializedProperty eventKey)
    {
        var clickable = ResolveBehaviorValue(behavior) as MapResClickable;
        var currentValue = clickable?.eventKey;
        if (currentValue == null && eventKey.propertyType == SerializedPropertyType.String)
            currentValue = eventKey.stringValue;

        if (!GUI.Button(rect, string.IsNullOrEmpty(currentValue) ? "无" : currentValue, EditorStyles.popup))
            return;

        var owner = eventKey.serializedObject?.targetObject;
        var propertyPath = eventKey.propertyPath;
        var menu = new GenericMenu();
        menu.AddItem(new GUIContent("无"), string.IsNullOrEmpty(currentValue),
            () => SetEventKeyValue(owner, propertyPath, clickable, string.Empty));

        foreach (var option in LoadMapStringOptions())
        {
            var value = option.Value;
            menu.AddItem(new GUIContent(option.Category + "/" + value), value == currentValue,
                () => SetEventKeyValue(owner, propertyPath, clickable, value));
        }

        menu.DropDown(rect);
    }

    private static void SetEventKeyValue(UnityEngine.Object owner, string propertyPath, MapResClickable clickable, string value)
    {
        if (owner == null)
            return;

        Undo.RecordObject(owner, EditorUndoPrefix + " Set Event Key");
        var serializedObject = new SerializedObject(owner);
        var property = serializedObject.FindProperty(propertyPath);
        if (property != null && property.propertyType == SerializedPropertyType.String)
        {
            property.stringValue = value;
        }
        else if (clickable != null)
        {
            clickable.eventKey = value;
        }

        serializedObject.ApplyModifiedProperties();
        EditorUtility.SetDirty(owner);
    }

    private static List<MapStringOption> LoadMapStringOptions()
    {
        var options = new List<MapStringOption>();
        var path = Path.Combine(Application.dataPath, "Scripts", "HotUpdate", "Logic", "Map", "MapStrings.cs");
        if (!File.Exists(path))
            return options;

        var source = File.ReadAllText(path);
        var assembly = typeof(MapBuilding).Assembly;
        foreach (Match classMatch in Regex.Matches(source, @"\bclass\s+([A-Za-z_][A-Za-z0-9_]*)"))
        {
            var typeName = classMatch.Groups[1].Value;
            var type = assembly.GetType(typeName);
            if (type == null)
                continue;

            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
            foreach (var field in type.GetFields(flags))
            {
                if (field.FieldType != typeof(string) || !field.IsLiteral || field.IsInitOnly)
                    continue;

                var value = field.GetRawConstantValue() as string;
                if (string.IsNullOrEmpty(value))
                    continue;

                options.Add(new MapStringOption { Category = type.Name, Value = value });
            }
        }

        return options;
    }

    private static bool ShouldShowBehaviorChild(SerializedProperty behavior, SerializedProperty child)
    {
        switch (child.name)
        {
            case "pressedScale":
            case "pressDuration":
            case "releaseDuration":
                var enableScaleEffect = behavior.FindPropertyRelative("enableScaleEffect");
                return enableScaleEffect == null || enableScaleEffect.boolValue;

            case "eventKey":
                var broadcastEvent = behavior.FindPropertyRelative("broadcastEvent");
                return broadcastEvent == null || broadcastEvent.boolValue;

            default:
                return true;
        }
    }

    private static object ResolveBehaviorValue(SerializedProperty behavior)
    {
        if (behavior == null || behavior.serializedObject == null)
            return null;

        object current = behavior.serializedObject.targetObject;
        foreach (var segment in behavior.propertyPath.Split('.'))
        {
            if (string.IsNullOrEmpty(segment) || segment == "Array")
                continue;

            if (segment.StartsWith("data[", StringComparison.Ordinal) &&
                segment.EndsWith("]", StringComparison.Ordinal))
            {
                var indexText = segment.Substring(5, segment.Length - 6);
                if (!int.TryParse(indexText, out var index) || current == null)
                    return null;

                current = GetArrayValue(current, index);
                continue;
            }

            current = GetFieldValue(current, segment);
        }

        return current;
    }

    private static object GetArrayValue(object value, int index)
    {
        if (value is Array array)
            return index >= 0 && index < array.Length ? array.GetValue(index) : null;

        if (value is System.Collections.IList list)
            return index >= 0 && index < list.Count ? list[index] : null;

        return null;
    }

    private static object GetFieldValue(object value, string fieldName)
    {
        if (value == null)
            return null;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var field = value.GetType().GetField(fieldName, flags);
        return field?.GetValue(value);
    }

    private static string DescribeBehavior(SerializedProperty property)
    {
        return ResolveBehaviorValue(property) is MapResBehavior behavior ? behavior.DisplayName : string.Empty;
    }

    private void ShowBehaviorTypeMenu(Rect buttonRect, SerializedProperty behaviors)
    {
        EnsureBehaviorTypeCache();
        if (_behaviorTypes.Length == 0)
        {
            EditorUtility.DisplayDialog(
                "Map Res Baker",
                "No concrete MapResBehavior subclass found.",
                "OK");
            return;
        }

        var menu = new GenericMenu();
        for (var i = 0; i < _behaviorTypes.Length; i++)
        {
            var type = _behaviorTypes[i];
            menu.AddItem(new GUIContent(_behaviorTypeLabels[i]), false, () =>
            {
                AddBehavior(behaviors, type);
            });
        }

        menu.DropDown(buttonRect);
    }

    private void AddBehavior(SerializedProperty behaviors, Type type)
    {
        Undo.RecordObject(target, EditorUndoPrefix + " Add Behavior");

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

    private EditableListState GetOrCreateListState(string key)
    {
        if (!_listStates.TryGetValue(key, out var state))
        {
            state = new EditableListState();
            _listStates[key] = state;
        }

        return state;
    }

    private static Type[] _behaviorTypes = Array.Empty<Type>();
    private static string[] _behaviorTypeLabels = Array.Empty<string>();

    private static void EnsureBehaviorTypeCache()
    {
        var types = new List<Type>();
        foreach (var type in TypeCache.GetTypesDerivedFrom<MapResBehavior>())
        {
            if (type.IsAbstract || type.IsGenericTypeDefinition)
                continue;

            if (type.GetConstructor(Type.EmptyTypes) == null)
                continue;

            types.Add(type);
        }

        types.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        _behaviorTypes = types.ToArray();

        _behaviorTypeLabels = new string[_behaviorTypes.Length];
        for (var i = 0; i < _behaviorTypes.Length; i++)
        {
            _behaviorTypeLabels[i] =
                ((MapResBehavior)Activator.CreateInstance(_behaviorTypes[i])).DisplayName;
        }
    }

    private readonly Dictionary<string, string> _behaviorSearchKeywords =
        new Dictionary<string, string>();

    private readonly Dictionary<string, EditableListState> _listStates =
        new Dictionary<string, EditableListState>();

    private sealed class ArrayView
    {
        public string FieldName;
        public string DisplayName;
        public string Search;
        public string CachedSearch;
        public ReorderableList List;
        public Func<SerializedProperty, string> Describe;
        public Action<Rect, SerializedProperty, int> DrawElement;
        public Func<SerializedProperty, float> ElementHeight;
    }

    private sealed class MapStringOption
    {
        public string Category;
        public string Value;
    }

    private sealed class EditableListState
    {
        public readonly List<int> VisibleIndices = new List<int>();
        public int SelectedRow = -1;
    }
}
#endif