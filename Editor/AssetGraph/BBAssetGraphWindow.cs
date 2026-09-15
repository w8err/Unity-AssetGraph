using System;
using System.Collections.Generic;
using System.Linq;

using UnityEditor;
using UnityEngine;

namespace BeastBlood.Editor.AssetGraph
{
    using Object = UnityEngine.Object;

    /// <summary>
    /// 참조 인덱스(<see cref="BBAssetRefIndex"/>)를 그래프로 보여주는 에디터 창. 언리얼 Reference Viewer에 해당한다.
    /// </summary>
    /// <remarks>
    /// 왼쪽은 대상을 쓰는 에셋(역참조), 오른쪽은 대상이 쓰는 에셋(정참조)이다. 화살표는 항상 "쓰는 쪽 → 쓰이는 쪽".
    /// 기본은 양쪽 1단계이고, 노드를 클릭하면 Project 창에서 핑하고, 더블클릭하면 그 파일이 탐색 대상이 된다. 대상 노드는 화면 가운데에 온다.
    /// </remarks>
    public class BBAssetGraphWindow : EditorWindow
    {
        #region Fields

        private const float MIN_NODE_WIDTH = 200f;
        private const float MAX_NODE_WIDTH = 420f;
        private const float NAME_LEFT_PADDING = 10f;
        private const float NAME_RIGHT_RESERVE = 64f; // 오른쪽 위 타입 글자 자리
        private const float NODE_HEIGHT = 40f;
        private const float COLUMN_GAP = 80f;
        private const float ROW_GAP = 10f;
        private const float HEADER_HEIGHT = 26f;
        private const float MARGIN = 24f;
        private const int MAX_NODES_PER_SIDE = 400;
        private const double BUILDING_REPAINT_INTERVAL = 0.5;

        [SerializeField] private string _targetGuid;
        [SerializeField] private bool _showSecondLevel;
        [SerializeField] private List<string> _hiddenExtensions = new List<string>(); // 그래프에서 숨길 확장자 (".cs" 등)
        [SerializeField] private bool _includeThirdParty;
        [SerializeField] private List<string> _history = new List<string>();
        [SerializeField] private Vector2 _scroll;

        private readonly List<BBAssetGraphNode> _nodes = new List<BBAssetGraphNode>();
        private readonly Dictionary<string, BBAssetGraphNode> _nodeByKey = new Dictionary<string, BBAssetGraphNode>();
        private readonly Dictionary<int, float> _columnX = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _columnWidth = new Dictionary<int, float>();
        private readonly Dictionary<string, int> _extensionCounts = new Dictionary<string, int>(); // 필터 전 확장자별 노드 수

        private bool _graphDirty = true;
        private bool _centerPending = true;
        private bool _truncated;
        private bool _wasBuilding;
        private double _lastBuildingRepaint;
        private int _refsCount;
        private int _depsCount;
        private Vector2 _contentSize;
        private int _minColumn;
        private int _maxColumn;
        private BBAssetGraphNode _hovered;

        private GUIStyle _nameStyle;
        private GUIStyle _rootNameStyle;
        private GUIStyle _dirStyle;
        private GUIStyle _typeStyle;
        private GUIStyle _headerStyle;

        #endregion Fields

        #region Unity Lifecycle

        private void OnEnable()
        {
            titleContent = new GUIContent("에셋 참조 그래프");
            wantsMouseMove = true;
            EditorApplication.update += OnEditorUpdate;
            BBAssetRefIndex.Changed += OnIndexChanged;
            _graphDirty = true;
            _centerPending = true;
        }

        private void OnDisable()
        {
            EditorApplication.update -= OnEditorUpdate;
            BBAssetRefIndex.Changed -= OnIndexChanged;
        }

        private void OnFocus()
        {
            // 창을 비운 사이 에셋이 바뀌었을 수 있다. 인덱스는 후처리기가 갱신하므로 그래프만 다시 만든다
            _graphDirty = true;
        }

        private void OnGUI()
        {
            EnsureStyles();
            DrawToolbar();

            var index = BBAssetRefIndex.Instance;
            if (index.IsBuilding)
            {
                EditorGUILayout.HelpBox("참조 인덱스를 만드는 중이에요. 처음 한 번은 30초 정도 걸려요.", MessageType.Info);
                return;
            }

            if (!index.IsBuilt)
            {
                EditorGUILayout.HelpBox("참조 인덱스가 아직 없어요. 프로젝트 전체를 한 번 스캔해야 그래프를 그릴 수 있어요.", MessageType.Info);
                if (GUILayout.Button("인덱스 만들기", GUILayout.Width(160)))
                {
                    index.StartBuild(false);
                }

                return;
            }

            if (string.IsNullOrEmpty(_targetGuid))
            {
                EditorGUILayout.HelpBox("위 칸에 에셋을 끌어다 놓거나, Project 창에서 에셋을 우클릭한 뒤 '에셋 참조 그래프 보기'를 누르세요.", MessageType.Info);
                return;
            }

            if (_graphDirty)
            {
                RebuildGraph();
            }

            if (_truncated)
            {
                EditorGUILayout.HelpBox($"한쪽에 {MAX_NODES_PER_SIDE}개가 넘어 나머지는 생략했어요. 2단계 표시를 끄거나 확장자 필터로 줄여 보세요.", MessageType.Warning);
            }

            if (IsTargetUnsaved())
            {
                EditorGUILayout.HelpBox("대상 파일에 저장 안 한 변경이 있어요. 그래프는 저장된 파일 기준이라 Ctrl+S로 저장해야 반영돼요.", MessageType.Warning);
            }

            DrawGraph();
        }

        #endregion Unity Lifecycle

        #region Public API

        [MenuItem("Assets/에셋 참조 그래프 보기", false, 30)]
        private static void OpenForSelection()
        {
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(Selection.activeObject, out var guid, out long _)) return;

            var window = GetWindow<BBAssetGraphWindow>();
            window.SetTarget(guid, true);
            window.Show();
        }

        [MenuItem("Assets/에셋 참조 그래프 보기", true)]
        private static bool ValidateOpenForSelection()
        {
            return Selection.activeObject != null && AssetDatabase.Contains(Selection.activeObject);
        }

        /// <summary>
        /// 그래프 대상을 바꾼다.
        /// </summary>
        /// <param name="guid">새 대상 에셋 GUID</param>
        /// <param name="pushHistory">true면 지금 대상을 뒤로 가기 기록에 남긴다</param>
        public void SetTarget(string guid, bool pushHistory)
        {
            if (string.IsNullOrEmpty(guid) || guid == _targetGuid) return;

            if (pushHistory && !string.IsNullOrEmpty(_targetGuid))
            {
                _history.Add(_targetGuid);
            }

            _targetGuid = guid;
            _graphDirty = true;
            _centerPending = true;
            Repaint();
        }

        #endregion Public API

        #region Graph

        private void RebuildGraph()
        {
            _graphDirty = false;
            _nodes.Clear();
            _nodeByKey.Clear();
            _hovered = null;
            _truncated = false;

            var index = BBAssetRefIndex.Instance;
            var root = new BBAssetGraphNode($"0:{_targetGuid}", _targetGuid, index.GetPath(_targetGuid), 0, null, false);
            AddNode(root);

            var depth = _showSecondLevel ? 2 : 1;
            var refs = index.Walk(_targetGuid, true, depth, _includeThirdParty);
            var deps = index.Walk(_targetGuid, false, depth, _includeThirdParty);

            _extensionCounts.Clear();
            foreach (var hit in refs.Concat(deps))
            {
                var key = ExtensionKey(hit.Path);
                _extensionCounts[key] = _extensionCounts.TryGetValue(key, out var count) ? count + 1 : 1;
            }

            _refsCount = AddSide(refs, -1, root);
            _depsCount = AddSide(deps, 1, root);
            LayoutNodes();
        }

        private int AddSide(List<FAssetRefHit> hits, int sign, BBAssetGraphNode root)
        {
            var hitByGuid = new Dictionary<string, FAssetRefHit>();
            foreach (var hit in hits)
            {
                hitByGuid[hit.Guid] = hit;
            }

            var added = 0;
            foreach (var hit in hits)
            {
                if (IsHidden(hit.Path)) continue;

                if (added >= MAX_NODES_PER_SIDE)
                {
                    _truncated = true;
                    break;
                }

                // 필터로 숨긴 중간 노드는 건너뛰고, 보이는 가장 가까운 조상에 선을 잇는다
                var parentGuid = hit.ParentGuid;
                while (parentGuid != root.Guid && hitByGuid.TryGetValue(parentGuid, out var parentHit) && IsHidden(parentHit.Path))
                {
                    parentGuid = parentHit.ParentGuid;
                }

                var parentKey = parentGuid == root.Guid ? root.Key : $"{sign}:{parentGuid}";
                AddNode(new BBAssetGraphNode($"{sign}:{hit.Guid}", hit.Guid, hit.Path, sign * hit.Depth, parentKey, hit.ViaCode));
                added++;
            }

            return added;
        }

        private void AddNode(BBAssetGraphNode node)
        {
            _nodes.Add(node);
            _nodeByKey[node.Key] = node;
        }

        /// <summary>
        /// 열은 거리, 열 안의 순서는 부모 순서 → 타입 → 이름. 부모가 가까운 열에 먼저 배치되도록 거리 순으로 정렬한다.
        /// </summary>
        private void LayoutNodes()
        {
            var columns = _nodes.GroupBy(node => node.Column).ToDictionary(group => group.Key, group => group.ToList());
            _minColumn = columns.Keys.Min();
            _maxColumn = columns.Keys.Max();

            foreach (var column in columns.Keys.OrderBy(key => Math.Abs(key)))
            {
                var list = columns[column];
                list.Sort(CompareInColumn);
                for (var i = 0; i < list.Count; i++)
                {
                    list[i].Order = i;
                }
            }

            // 열 너비: 확장자를 뺀 이름 중 가장 긴 것에 맞추되 상·하한을 둔다
            _columnX.Clear();
            _columnWidth.Clear();
            var nextX = MARGIN;
            for (var column = _minColumn; column <= _maxColumn; column++)
            {
                var width = MIN_NODE_WIDTH;
                if (columns.TryGetValue(column, out var members))
                {
                    foreach (var node in members)
                    {
                        var nameWidth = NameStyle(node).CalcSize(new GUIContent(DisplayName(node))).x;
                        width = Mathf.Max(width, NAME_LEFT_PADDING + nameWidth + NAME_RIGHT_RESERVE);
                    }
                }

                width = Mathf.Min(width, MAX_NODE_WIDTH);
                _columnX[column] = nextX;
                _columnWidth[column] = width;
                nextX += width + COLUMN_GAP;
            }

            var step = NODE_HEIGHT + ROW_GAP;
            var maxRows = columns.Values.Max(list => list.Count);
            foreach (var pair in columns)
            {
                var columnWidth = _columnWidth[pair.Key];
                var top = MARGIN + HEADER_HEIGHT + (maxRows - pair.Value.Count) * step * 0.5f;
                foreach (var node in pair.Value)
                {
                    node.Rect = new Rect(_columnX[pair.Key], top + node.Order * step, columnWidth, NODE_HEIGHT);
                    node.Label = Ellipsize(DisplayName(node), NameStyle(node), columnWidth - NAME_LEFT_PADDING - NAME_RIGHT_RESERVE);
                }
            }

            _contentSize = new Vector2(nextX - COLUMN_GAP + MARGIN, MARGIN * 2f + HEADER_HEIGHT + maxRows * step);
        }

        private int CompareInColumn(BBAssetGraphNode a, BBAssetGraphNode b)
        {
            var parentOrder = ParentOrder(a).CompareTo(ParentOrder(b));
            if (parentOrder != 0) return parentOrder;

            var typeOrder = GetTypeInfo(a.Path).Order.CompareTo(GetTypeInfo(b.Path).Order);
            if (typeOrder != 0) return typeOrder;

            return string.Compare(DisplayName(a), DisplayName(b), StringComparison.OrdinalIgnoreCase);
        }

        private int ParentOrder(BBAssetGraphNode node)
        {
            return node.ParentKey != null && _nodeByKey.TryGetValue(node.ParentKey, out var parent) ? parent.Order : 0;
        }

        #endregion Graph

        #region Drawing

        private void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                using (new EditorGUI.DisabledScope(_history.Count == 0))
                {
                    if (GUILayout.Button(new GUIContent("◀", "이전 대상으로 돌아가기"), EditorStyles.toolbarButton, GUILayout.Width(26)))
                    {
                        var previous = _history[_history.Count - 1];
                        _history.RemoveAt(_history.Count - 1);
                        SetTarget(previous, false);
                    }
                }

                var index = BBAssetRefIndex.Instance;
                var path = index.GetPath(_targetGuid);
                var current = path != null ? AssetDatabase.LoadAssetAtPath<Object>(path) : null;
                var picked = EditorGUILayout.ObjectField(current, typeof(Object), false, GUILayout.Width(260));
                if (picked != null && picked != current
                    && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(picked, out var pickedGuid, out long _))
                {
                    SetTarget(pickedGuid, true);
                }

                GUILayout.Space(8);
                _showSecondLevel = ToolbarToggle(_showSecondLevel, "2단계 표시", "참조하는 것의 참조까지 한 단계 더 보여줘요");
                DrawExtensionFilterButton();
                _includeThirdParty = ToolbarToggle(_includeThirdParty, "서드파티", "20_Plugins · 30_ThirdParty · Plugins · Packages 포함");

                GUILayout.FlexibleSpace();

                if (!string.IsNullOrEmpty(_targetGuid) && index.IsBuilt && !index.IsBuilding)
                {
                    GUILayout.Label($"쓰는 곳 {_refsCount} · 쓰는 것 {_depsCount}", EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
                }

                using (new EditorGUI.DisabledScope(index.IsBuilding || !index.IsBuilt || _nodes.Count == 0))
                {
                    if (GUILayout.Button(new GUIContent("새로고침", "지금 그래프에 보이는 파일만 다시 읽어요 (바로 끝나요). 저장한 변경이 안 보일 때 눌러요."),
                            EditorStyles.toolbarButton, GUILayout.ExpandWidth(false)))
                    {
                        RefreshVisibleFiles();
                    }
                }

                using (new EditorGUI.DisabledScope(index.IsBuilding))
                {
                    if (GUILayout.Button(new GUIContent("전체 재스캔", "캐시를 무시하고 프로젝트 전체를 다시 스캔해요 (30초 이상). 평소에는 에셋이 바뀔 때 자동 반영돼요."),
                            EditorStyles.toolbarButton, GUILayout.ExpandWidth(false)))
                    {
                        index.StartBuild(true);
                    }
                }
            }
        }

        private void DrawExtensionFilterButton()
        {
            var visible = _extensionCounts.Keys.Count(key => !_hiddenExtensions.Contains(key));
            var text = visible == _extensionCounts.Count ? "확장자: 전체" : $"확장자: {visible}/{_extensionCounts.Count}";
            var content = new GUIContent(text, "그래프에 띄울 확장자를 골라요");
            var rect = GUILayoutUtility.GetRect(content, EditorStyles.toolbarDropDown, GUILayout.ExpandWidth(false));
            if (GUI.Button(rect, content, EditorStyles.toolbarDropDown))
            {
                ShowExtensionMenu(rect);
            }
        }

        /// <summary>
        /// 확장자별 표시 여부를 켜고 끄는 메뉴. '이것만 보기'는 그 확장자 외를 전부 숨긴다.
        /// </summary>
        private void ShowExtensionMenu(Rect buttonRect)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("전체 표시"), _hiddenExtensions.Count == 0, () => SetHiddenExtensions(new List<string>()));

            var ordered = _extensionCounts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).ToList();
            if (ordered.Count > 0)
            {
                menu.AddSeparator(string.Empty);
            }

            foreach (var pair in ordered)
            {
                var extension = pair.Key;
                menu.AddItem(new GUIContent($"{extension}  ({pair.Value})"), !_hiddenExtensions.Contains(extension), () => ToggleExtension(extension));
            }

            if (ordered.Count > 1)
            {
                menu.AddSeparator(string.Empty);
                foreach (var pair in ordered)
                {
                    var extension = pair.Key;
                    menu.AddItem(new GUIContent($"이것만 보기/{extension}  ({pair.Value})"), false,
                        () => SetHiddenExtensions(_extensionCounts.Keys.Where(key => key != extension).ToList()));
                }
            }

            menu.DropDown(buttonRect);
        }

        private void ToggleExtension(string extension)
        {
            var next = new List<string>(_hiddenExtensions);
            if (!next.Remove(extension))
            {
                next.Add(extension);
            }

            SetHiddenExtensions(next);
        }

        private void SetHiddenExtensions(List<string> hidden)
        {
            _hiddenExtensions = hidden;
            _graphDirty = true;
            Repaint();
        }

        private bool ToolbarToggle(bool value, string label, string tooltip)
        {
            var next = GUILayout.Toggle(value, new GUIContent(label, tooltip), EditorStyles.toolbarButton, GUILayout.ExpandWidth(false));
            if (next != value)
            {
                _graphDirty = true;
            }

            return next;
        }

        private void DrawGraph()
        {
            GUILayout.Label("클릭: Project 창에서 핑  ·  더블클릭: 그 파일을 탐색 대상으로  ·  ◀: 이전 대상  ·  우클릭/휠 드래그: 화면 이동  ·  주황 점선: 코드 문자열로 추정한 연결",
                EditorStyles.miniLabel);

            var area = GUILayoutUtility.GetRect(0f, 0f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            EditorGUI.DrawRect(area, CanvasColor);

            // 그래프가 창보다 작으면 가운데에 놓고, 크면 대상 노드가 화면 가운데 오도록 스크롤을 맞춘다
            var canvasSize = new Vector2(Mathf.Max(_contentSize.x, area.width - 16f), Mathf.Max(_contentSize.y, area.height - 16f));
            var offset = (canvasSize - _contentSize) * 0.5f;
            if (_centerPending && Event.current.type == EventType.Repaint && _nodes.Count > 0 && area.width > 1f)
            {
                _centerPending = false;
                var rootCenter = _nodes[0].Rect.center + offset;
                _scroll = new Vector2(
                    Mathf.Clamp(rootCenter.x - area.width * 0.5f, 0f, Mathf.Max(0f, canvasSize.x - area.width)),
                    Mathf.Clamp(rootCenter.y - area.height * 0.5f, 0f, Mathf.Max(0f, canvasSize.y - area.height)));
            }

            _scroll = GUI.BeginScrollView(area, _scroll, new Rect(0f, 0f, canvasSize.x, canvasSize.y));
            GUI.BeginGroup(new Rect(offset, _contentSize));
            HandleInput(Event.current);

            if (Event.current.type == EventType.Repaint)
            {
                DrawColumnHeaders();
                DrawEdges();
            }

            foreach (var node in _nodes)
            {
                DrawNode(node);
            }

            GUI.EndGroup();
            GUI.EndScrollView();
        }

        private void DrawColumnHeaders()
        {
            foreach (var pair in _columnX)
            {
                var column = pair.Key;
                var label = column switch
                {
                    0 => "탐색 대상",
                    -1 => "이 파일을 쓰는 곳",
                    1 => "이 파일이 쓰는 것",
                    _ => column < 0 ? "쓰는 곳 · 2단계" : "쓰는 것 · 2단계",
                };
                GUI.Label(new Rect(pair.Value, MARGIN, _columnWidth[column], HEADER_HEIGHT - 6f), label, _headerStyle);
            }
        }

        private void DrawEdges()
        {
            foreach (var node in _nodes)
            {
                if (node.ParentKey == null || !_nodeByKey.TryGetValue(node.ParentKey, out var parent)) continue;

                // 역참조 쪽은 노드가 부모를 쓰고, 정참조 쪽은 부모가 노드를 쓴다
                var user = node.Column < 0 ? node : parent;
                var used = node.Column < 0 ? parent : node;
                var start = new Vector3(user.Rect.xMax, user.Rect.center.y);
                var end = new Vector3(used.Rect.xMin - 1f, used.Rect.center.y);
                var tangent = Vector3.right * Mathf.Max(30f, (end.x - start.x) * 0.5f);

                var highlighted = _hovered == node || _hovered == parent;
                var color = node.ViaCode ? CodeEdgeColor : EdgeColor;
                if (_hovered != null && !highlighted)
                {
                    color.a *= 0.12f;
                }

                var width = highlighted ? 3.5f : 2f;
                if (node.ViaCode)
                {
                    DrawDashedBezier(start, end, start + tangent, end - tangent, color, width);
                }
                else
                {
                    Handles.DrawBezier(start, end, start + tangent, end - tangent, color, null, width);
                }

                Handles.color = color;
                Handles.DrawAAConvexPolygon(end, end + new Vector3(-9f, -4.5f), end + new Vector3(-9f, 4.5f));
            }
        }

        private void DrawNode(BBAssetGraphNode node)
        {
            var rect = node.Rect;
            var info = GetTypeInfo(node.Path);
            var isRoot = node.Column == 0;
            var dim = node.Path != null && (node.Path.Contains("/Obsoleted/") || node.Path.Contains("/NotUsed/"));
            var alpha = _hovered != null && !IsConnectedToHovered(node) ? 0.35f : dim ? 0.6f : 1f;

            if (Event.current.type == EventType.Repaint)
            {
                var border = _hovered == node ? AccentColor : BorderColor;
                EditorGUI.DrawRect(rect, WithAlpha(border, alpha));
                var inset = _hovered == node ? 2f : 1f;
                EditorGUI.DrawRect(new Rect(rect.x + inset, rect.y + inset, rect.width - inset * 2f, rect.height - inset * 2f),
                    WithAlpha(isRoot ? AccentColor : NodeColor, alpha));
                EditorGUI.DrawRect(new Rect(rect.x + inset, rect.y + inset, 4f, rect.height - inset * 2f), WithAlpha(info.Color, alpha));
            }

            var previousColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, alpha);

            _typeStyle.normal.textColor = isRoot ? Color.white : info.Color;
            GUI.Label(new Rect(rect.xMax - 58f, rect.y + 3f, 52f, 14f), info.Label, _typeStyle);
            GUI.Label(new Rect(rect.x + NAME_LEFT_PADDING, rect.y + 3f, rect.width - NAME_LEFT_PADDING - NAME_RIGHT_RESERVE, 18f),
                node.Label ?? DisplayName(node), NameStyle(node));
            GUI.Label(new Rect(rect.x + 10f, rect.y + 21f, rect.width - 16f, 15f), ShortFolder(node), _dirStyle);

            var tooltip = (node.Path ?? $"프로젝트에 없는 GUID {node.Guid}") + (node.ViaCode ? "\n(코드 문자열로 추정한 연결)" : string.Empty);
            GUI.Label(rect, new GUIContent(string.Empty, tooltip));

            GUI.color = previousColor;
        }

        private static void DrawDashedBezier(Vector3 start, Vector3 end, Vector3 startTangent, Vector3 endTangent, Color color, float width)
        {
            const int SEGMENTS = 24;

            var points = Handles.MakeBezierPoints(start, end, startTangent, endTangent, SEGMENTS + 1);
            Handles.color = color;
            for (var i = 0; i < SEGMENTS; i += 2)
            {
                Handles.DrawAAPolyLine(width, points[i], points[i + 1]);
            }
        }

        #endregion Drawing

        #region Input

        private void HandleInput(Event current)
        {
            switch (current.type)
            {
                case EventType.MouseMove:
                {
                    var hit = HitTest(current.mousePosition);
                    if (hit != _hovered)
                    {
                        _hovered = hit;
                        Repaint();
                    }

                    break;
                }

                case EventType.MouseDown when current.button == 0:
                {
                    var hit = HitTest(current.mousePosition);
                    if (hit == null) break;

                    OnNodeClicked(hit, current.clickCount);
                    current.Use();
                    break;
                }

                case EventType.MouseDrag when current.button == 1 || current.button == 2:
                    _scroll -= current.delta;
                    current.Use();
                    Repaint();
                    break;

                case EventType.MouseLeaveWindow:
                    _hovered = null;
                    Repaint();
                    break;
            }
        }

        private BBAssetGraphNode HitTest(Vector2 position)
        {
            foreach (var node in _nodes)
            {
                if (node.Rect.Contains(position)) return node;
            }

            return null;
        }

        /// <summary>
        /// 한 번 클릭하면 Project 창에서 핑만 하고, 더블클릭하면 그 파일을 탐색 대상으로 삼는다.
        /// </summary>
        private void OnNodeClicked(BBAssetGraphNode node, int clickCount)
        {
            if (node.Path == null) return;

            var asset = AssetDatabase.LoadAssetAtPath<Object>(node.Path);
            if (asset == null) return;

            if (clickCount >= 2)
            {
                if (node.Column != 0)
                {
                    SetTarget(node.Guid, true);
                }

                return;
            }

            EditorGUIUtility.PingObject(asset);
        }

        /// <summary>
        /// 보이는 노드의 파일(대상 포함)만 인덱스에 다시 읽힌다. 전체 재스캔 없이 최신 상태로 맞추는 용도.
        /// </summary>
        private void RefreshVisibleFiles()
        {
            var paths = _nodes
                .Where(node => node.Path != null && node.Path.StartsWith("Assets/", StringComparison.Ordinal))
                .Select(node => node.Path)
                .Distinct()
                .ToList();

            var refreshed = BBAssetRefIndex.Instance.RefreshFiles(paths);
            ShowNotification(new GUIContent(refreshed ? $"파일 {paths.Count}개를 다시 읽었어요" : "전체 스캔 중이라 끝난 뒤에 반영돼요"));
            _graphDirty = true;
            Repaint();
        }

        private void OnIndexChanged()
        {
            _graphDirty = true;
            Repaint();
        }

        private void OnEditorUpdate()
        {
            var building = BBAssetRefIndex.HasCache && BBAssetRefIndex.Instance.IsBuilding;
            if (_wasBuilding && !building)
            {
                _graphDirty = true;
                Repaint();
            }
            else if (building && EditorApplication.timeSinceStartup - _lastBuildingRepaint > BUILDING_REPAINT_INTERVAL)
            {
                _lastBuildingRepaint = EditorApplication.timeSinceStartup;
                Repaint();
            }

            _wasBuilding = building;
        }

        #endregion Input

        #region Helper

        private bool IsConnectedToHovered(BBAssetGraphNode node)
        {
            if (node == _hovered) return true;

            return node.ParentKey == _hovered.Key || _hovered.ParentKey == node.Key;
        }

        private bool IsHidden(string path)
        {
            return _hiddenExtensions.Contains(ExtensionKey(path));
        }

        private static string ExtensionKey(string path)
        {
            if (path == null) return "(끊긴 참조)";

            var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
            return string.IsNullOrEmpty(extension) ? "(확장자 없음)" : extension;
        }

        private bool IsTargetUnsaved()
        {
            var path = BBAssetRefIndex.Instance.GetPath(_targetGuid);
            if (path == null || !path.StartsWith("Assets/", StringComparison.Ordinal)) return false;

            var asset = AssetDatabase.LoadAssetAtPath<Object>(path);
            return asset != null && EditorUtility.IsDirty(asset);
        }

        /// <summary>
        /// 노드 이름. 타입은 색 띠·타입 글자로 이미 보이므로 확장자는 뺀다.
        /// </summary>
        private static string DisplayName(BBAssetGraphNode node)
        {
            return node.Path != null ? System.IO.Path.GetFileNameWithoutExtension(node.Path) : "(없는 에셋)";
        }

        private GUIStyle NameStyle(BBAssetGraphNode node)
        {
            return node.Column == 0 ? _rootNameStyle : _nameStyle;
        }

        /// <summary>
        /// 폭에 안 들어가면 앞뒤를 남기고 가운데를 줄인다. 번호·접미사(_Enhanced, 01 등)가 보이게 하려는 것.
        /// </summary>
        private static string Ellipsize(string text, GUIStyle style, float maxWidth)
        {
            if (style.CalcSize(new GUIContent(text)).x <= maxWidth) return text;

            for (var keep = text.Length - 1; keep > 2; keep--)
            {
                var head = (keep + 1) / 2;
                var candidate = $"{text.Substring(0, head)}…{text.Substring(text.Length - (keep - head))}";
                if (style.CalcSize(new GUIContent(candidate)).x <= maxWidth) return candidate;
            }

            return "…";
        }

        private static string ShortFolder(BBAssetGraphNode node)
        {
            if (node.Path == null) return node.Guid;

            var folder = System.IO.Path.GetDirectoryName(node.Path)?.Replace('\\', '/') ?? string.Empty;
            return folder.Length > 42 ? $"…{folder.Substring(folder.Length - 41)}" : folder;
        }

        private static (string Label, Color Color, int Order) GetTypeInfo(string path)
        {
            if (path == null) return ("없음", Pick("#C93B3B", "#F07878"), 11);
            if (path.StartsWith("ProjectSettings/", StringComparison.Ordinal)) return ("설정", Pick("#9A7427", "#D3AE5E"), 9);

            switch (System.IO.Path.GetExtension(path).ToLowerInvariant())
            {
                case ".unity": return ("씬", Pick("#16896A", "#4CC39C"), 0);
                case ".prefab": return ("프리팹", Pick("#3A6FD8", "#7EA6FF"), 1);
                case ".asset": return ("SO", Pick("#8A55C9", "#B990F0"), 2);
                case ".controller":
                case ".overridecontroller":
                case ".anim":
                case ".fbx": return ("애니·모델", Pick("#B4531F", "#F0935C"), 3);
                case ".playable": return ("타임라인", Pick("#0F7C8C", "#4FC6D6"), 4);
                case ".mat":
                case ".shader":
                case ".shadergraph": return ("머티리얼", Pick("#A63D7A", "#E27DB8"), 5);
                case ".png":
                case ".psd":
                case ".jpg":
                case ".tga": return ("텍스처", Pick("#5E7D2A", "#A5C969"), 6);
                case ".wav":
                case ".mp3":
                case ".ogg": return ("오디오", Pick("#7A5C3A", "#C9A67E"), 7);
                case ".cs": return ("C#", Pick("#667085", "#9AA4B5"), 8);
                default: return ("기타", Pick("#667085", "#9AA4B5"), 10);
            }
        }

        private static Color Pick(string light, string dark)
        {
            ColorUtility.TryParseHtmlString(EditorGUIUtility.isProSkin ? dark : light, out var color);
            return color;
        }

        private static Color WithAlpha(Color color, float alpha)
        {
            color.a *= alpha;
            return color;
        }

        private static Color CanvasColor => Pick("#E9ECF1", "#1B1F26");

        private static Color NodeColor => Pick("#FFFFFF", "#2A303A");

        private static Color BorderColor => Pick("#C6CDD8", "#3D4553");

        private static Color AccentColor => Pick("#2F5FC4", "#4C78D8");

        private static Color EdgeColor => Pick("#7D8898", "#7C8799");

        private static Color CodeEdgeColor => Pick("#C98A12", "#E8B04A");

        private void EnsureStyles()
        {
            if (_nameStyle != null) return;

            _nameStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 12, clipping = TextClipping.Clip };
            _rootNameStyle = new GUIStyle(_nameStyle);
            _rootNameStyle.normal.textColor = Color.white;
            _dirStyle = new GUIStyle(EditorStyles.miniLabel) { clipping = TextClipping.Clip };
            _typeStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.UpperRight, fontStyle = FontStyle.Bold };
            _headerStyle = new GUIStyle(EditorStyles.miniBoldLabel);
        }

        #endregion Helper
    }
}
