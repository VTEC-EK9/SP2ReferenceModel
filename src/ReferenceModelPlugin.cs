using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Assets.Scripts.Design;
using Assets.Scripts.Design.Tools;
using BepInEx;
using BepInEx.Configuration;
using Ookii.Dialogs;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;
using WinForms = System.Windows.Forms;

namespace SP2ReferenceModel
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class ReferenceModelPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "codex.sp2.referencemodel";
        public const string PluginName = "SP2 Reference Model";
        public const string PluginVersion = "0.7.0";
        private const string MenuButtonTitle = "OPEN .OBJ";

        private ConfigEntry<string> _modelPath;
        private ConfigEntry<float> _scale;
        private ConfigEntry<bool> _swapYz;
        private ConfigEntry<bool> _autoLoad;
        private ConfigEntry<bool> _showTextures;

        private GameObject _root;
        private readonly List<GameObject> _meshObjects = new List<GameObject>();
        private readonly Dictionary<MeshRenderer, Material[]> _texturedMaterials =
            new Dictionary<MeshRenderer, Material[]>();
        private readonly List<UnityEngine.Object> _ownedResources = new List<UnityEngine.Object>();
        private Material _whiteMaterial;
        private string _status = "No model loaded";
        private bool _modelVisible = true;
        private bool _triedAutoLoad;
        private string _loadedModelPath;
        private readonly List<string> _deletedNames = new List<string>();
        private bool _restoringState;

        private DesignerTools _hookedTools;
        private bool _editingModel;
        private Transform _editTarget;

        private bool _injected;
        private bool _panelVisible;
        private float _injectTimer;
        private GameObject _section;
        private Button _toggleButton;
        private TextMeshProUGUI _toggleLabel;
        private TextMeshProUGUI _statusLabel;
        private TextMeshProUGUI _pathLabel;
        private TextMeshProUGUI _editLabel;
        private TextMeshProUGUI _visibilityLabel;
        private TextMeshProUGUI _texturesLabel;
        private TextMeshProUGUI _swapLabel;
        private TextMeshProUGUI _autoLoadLabel;
        private TextMeshProUGUI _meshManagerLabel;
        private TMP_InputField _scaleInput;
        private TMP_FontAsset _font;
        private Material _fontMaterial;
        private float _fontSize = 14f;

        private Canvas _rootCanvas;
        private Button _templateButton;
        private GameObject _meshWindow;
        private Transform _meshRowsParent;
        private TextMeshProUGUI _meshWindowTitle;
        private TMP_InputField _meshFilterInput;
        private readonly List<MeshRow> _meshRows = new List<MeshRow>();
        private Material _outlineMaterial;
        private GameObject _hoverOutline;
        private GameObject _hoverTarget;

        private sealed class MeshRow
        {
            public GameObject RowObject;
            public GameObject Mesh;
            public TextMeshProUGUI Label;
        }

        // uGUI enter/exit events run on the whole parent chain of the hovered
        // object, so one handler on the row covers its name/Move/delete buttons.
        private sealed class RowHoverHandler : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
        {
            public Action<bool> HoverChanged;

            public void OnPointerEnter(PointerEventData eventData) => HoverChanged?.Invoke(true);
            public void OnPointerExit(PointerEventData eventData) => HoverChanged?.Invoke(false);
            private void OnDisable() => HoverChanged?.Invoke(false);
        }

        // Keeps the outline shell glued to its piece (pieces can be gizmo-moved
        // while hovered) by mirroring the piece's local pose, inflated about the
        // mesh bounds center.
        private sealed class OutlineFollower : MonoBehaviour
        {
            public Transform Target;
            public Vector3 BoundsCenter;
            public Vector3 Inflate;

            private void LateUpdate()
            {
                if (Target == null)
                {
                    gameObject.SetActive(false);
                    return;
                }
                transform.localRotation = Target.localRotation;
                transform.localScale = Vector3.Scale(Target.localScale, Inflate);
                Vector3 shift = BoundsCenter - Vector3.Scale(Inflate, BoundsCenter);
                transform.localPosition = Target.localPosition +
                    Target.localRotation * Vector3.Scale(Target.localScale, shift);
            }
        }

        private sealed class WindowDragHandle : MonoBehaviour, IDragHandler
        {
            public RectTransform Target;

            public void OnDrag(PointerEventData eventData)
            {
                if (Target == null) return;
                Canvas canvas = Target.GetComponentInParent<Canvas>();
                float scale = canvas != null && canvas.scaleFactor > 0f ? canvas.scaleFactor : 1f;
                Target.anchoredPosition += eventData.delta / scale;
            }
        }

        private string ModelDirectory => Path.Combine(Paths.ConfigPath, "SP2ReferenceModel", "Models");
        private string StateDirectory => Path.Combine(Paths.ConfigPath, "SP2ReferenceModel", "State");

        private void Awake()
        {
            Directory.CreateDirectory(ModelDirectory);
            _modelPath = Config.Bind("Model", "Path", Path.Combine(ModelDirectory, "model.obj"),
                "Absolute path to the OBJ reference model.");
            _scale = Config.Bind("Model", "Scale", 1f,
                "Reference model scale. GameModels3D OBJ exports are normally meter-scaled.");
            _swapYz = Config.Bind("Model", "SwapYAndZ", true,
                "Convert common Z-up OBJ coordinates to Unity Y-up coordinates.");
            _autoLoad = Config.Bind("Model", "AutoLoad", false,
                "Load the configured OBJ automatically when the designer opens.");
            _showTextures = Config.Bind("Model", "ShowTextures", false,
                "Render the MTL colors and textures. When off the model is plain white.");
            Logger.LogInfo(PluginName + " " + PluginVersion +
                           " loaded. Controls are injected into the designer main menu.");
        }

        private void OnDestroy()
        {
            SaveModelState();
        }

        private void Update()
        {
            Designer designer = Designer.Instance;
            if (designer == null)
            {
                _triedAutoLoad = false;
                _editingModel = false;
                _editTarget = null;
                UnhookTools();
                ResetInjectedUi();
                if (_root != null) UnloadModel(false);
                return;
            }

            HookTools(designer.Tools);

            if (_autoLoad.Value && !_triedAutoLoad)
            {
                _triedAutoLoad = true;
                if (File.Exists(_modelPath.Value)) LoadModel(_modelPath.Value);
            }

            if (_injected && (_toggleButton == null || _section == null)) ResetInjectedUi();
            if (_injected && _toggleLabel != null && _toggleLabel.text != MenuButtonTitle)
                _toggleLabel.text = MenuButtonTitle;

            if (!_injected)
            {
                _injectTimer += Time.unscaledDeltaTime;
                if (_injectTimer >= 0.5f)
                {
                    _injectTimer = 0f;
                    TryInjectUi();
                }
            }
        }

        // ------------------------------------------------------------------
        // Native gizmo editing (the translate/rotate tools behind hotkeys 2/3)
        // ------------------------------------------------------------------

        private void HookTools(DesignerTools tools)
        {
            if (tools == null || _hookedTools == tools) return;
            UnhookTools();
            _hookedTools = tools;
            tools.SelectedToolChanged += OnSelectedToolChanged;
        }

        private void UnhookTools()
        {
            if (_hookedTools != null)
            {
                _hookedTools.SelectedToolChanged -= OnSelectedToolChanged;
                _hookedTools = null;
            }
        }

        private void OnSelectedToolChanged(object sender, ToolChangedEventArgs e)
        {
            if (!_editingModel) return;
            DesignerTools tools = _hookedTools;
            if (tools == null || _editTarget == null)
            {
                _editingModel = false;
                _editTarget = null;
                return;
            }
            if (e.NewTool == tools.TranslateTool || e.NewTool == tools.RotateTool)
            {
                ArmGizmoTool((TransformTool)e.NewTool);
            }
            else
            {
                _editingModel = false;
                _editTarget = null;
            }
            RefreshLabels();
        }

        private void ArmGizmoTool(TransformTool tool)
        {
            tool.SetExternalTarget(_editTarget, null, OnGizmoEditDone, null, _editTarget);
        }

        // Fires on Done/Cancel and on tool switches; the pose is final either
        // way (Cancel restores the original pose before the callback).
        private void OnGizmoEditDone(bool applied)
        {
            SaveModelState();
        }

        private void StartEdit(Transform target)
        {
            Designer designer = Designer.Instance;
            if (designer == null || target == null) return;
            designer.DeselectPart();
            _editTarget = target;
            _editingModel = true;
            TransformTool tool = designer.Tools.SelectedTool as TransformTool ?? designer.Tools.TranslateTool;
            ArmGizmoTool(tool);
            designer.Tools.SelectTool(tool);
            designer.ShowMessage("Editing '" + target.name + "'.\nPress 2 to move, 3 to rotate. Done applies, Esc cancels.");
            RefreshLabels();
        }

        private void ToggleModelEdit()
        {
            Designer designer = Designer.Instance;
            if (designer == null) return;
            if (_editingModel)
            {
                designer.Tools.SelectMovePartTool();
                return;
            }
            if (_root == null)
            {
                SetStatus("Load a model first");
                return;
            }
            StartEdit(_root.transform);
        }

        private void EndModelEdit()
        {
            if (!_editingModel) return;
            _editingModel = false;
            _editTarget = null;
            Designer designer = Designer.Instance;
            if (designer != null && designer.Tools != null && designer.Tools.SelectedTool is TransformTool)
                designer.Tools.SelectMovePartTool();
        }

        // ------------------------------------------------------------------
        // UI injection
        // ------------------------------------------------------------------

        private void TryInjectUi()
        {
            GameObject flyout = FindMainMenuFlyout();
            if (flyout == null) return;
            Button template = FindButtonTemplate(flyout);
            if (template == null) return;

            GameObject toggleObject = null;
            try
            {
                CaptureNativeTextStyle(flyout, template);
                DestroyExistingInjectedUi(flyout);

                _templateButton = template;
                Canvas canvas = flyout.GetComponentInParent<Canvas>();
                _rootCanvas = canvas != null ? canvas.rootCanvas : null;

                toggleObject = CloneNativeButton(template, "ReferenceModelMenuButton",
                    MenuButtonTitle, template.transform.parent);
                MatchTemplateHeight(template, toggleObject);
                _toggleButton = toggleObject.GetComponent<Button>();
                _toggleLabel = toggleObject.GetComponentInChildren<TextMeshProUGUI>(true);
                _toggleButton.onClick.RemoveAllListeners();
                _toggleButton.onClick.AddListener(TogglePanel);
                toggleObject.transform.SetAsLastSibling();

                BuildInjectedPanel(toggleObject.transform.parent, template);
                _section.transform.SetAsLastSibling();
                _section.SetActive(false);
                _panelVisible = false;
                _injected = true;
                RefreshLabels();
                Logger.LogInfo("Injected " + MenuButtonTitle + " into the designer main menu.");
            }
            catch (Exception ex)
            {
                Logger.LogError("Reference model UI injection failed: " + ex);
                if (_section != null) Destroy(_section);
                if (toggleObject != null) Destroy(toggleObject);
                ResetInjectedUi();
            }
        }

        private static void MatchTemplateHeight(Button template, GameObject clone)
        {
            float height = ((RectTransform)template.transform).rect.height;
            if (height < 10f) height = 40f;
            LayoutElement element = clone.GetComponent<LayoutElement>() ?? clone.AddComponent<LayoutElement>();
            element.preferredHeight = height;
            element.minHeight = height;
            ((RectTransform)clone.transform).SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, height);
        }

        private void BuildInjectedPanel(Transform parent, Button template)
        {
            _section = new GameObject("ReferenceModelSection");
            _section.transform.SetParent(parent, false);
            _section.AddComponent<RectTransform>();
            VerticalLayoutGroup layout = _section.AddComponent<VerticalLayoutGroup>();
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.spacing = 5f;
            layout.padding = new RectOffset(8, 8, 8, 8);
            _section.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            TextMeshProUGUI heading = MakeText("3D REFERENCE MODEL", _section.transform, _fontSize * 0.82f, 24f);
            heading.alignment = TextAlignmentOptions.Center;
            _statusLabel = MakeText(_status, _section.transform, _fontSize * 0.7f, 34f);
            _statusLabel.alignment = TextAlignmentOptions.Center;
            _statusLabel.color = new Color(0.7f, 0.84f, 0.95f, 1f);
            _pathLabel = MakeText("", _section.transform, _fontSize * 0.62f, 30f);
            _pathLabel.alignment = TextAlignmentOptions.Center;
            _pathLabel.textWrappingMode = TextWrappingModes.Normal;

            GameObject loadRow = MakeRow("ReferenceLoadRow", _section.transform, 34f);
            AddActionButton(template, loadRow.transform, "Browse...", 100f, OpenObjFromDisk);
            AddActionButton(template, loadRow.transform, "Reload", 76f, ReloadModel);
            AddActionButton(template, loadRow.transform, "Unload", 76f, () => UnloadModel());

            _editLabel = AddActionButton(template, _section.transform, "", 220f, ToggleModelEdit)
                .GetComponentInChildren<TextMeshProUGUI>(true);
            _meshManagerLabel = AddActionButton(template, _section.transform, "", 220f, OpenMeshWindow)
                .GetComponentInChildren<TextMeshProUGUI>(true);

            GameObject scaleRow = MakeRow("ReferenceScaleRow", _section.transform, 32f);
            TextMeshProUGUI scaleCaption = MakeText("Scale", scaleRow.transform, _fontSize * 0.68f, 30f);
            scaleCaption.alignment = TextAlignmentOptions.Left;
            scaleCaption.gameObject.GetComponent<LayoutElement>().preferredWidth = 150f;
            _scaleInput = MakeTextInput(template, scaleRow.transform, FormatNumber(_scale.Value),
                TMP_InputField.ContentType.DecimalNumber, null, SetScaleValue);

            AddActionButton(template, _section.transform, "Reset transform", 220f, ResetTransform);

            _visibilityLabel = AddActionButton(template, _section.transform, "", 220f, ToggleModelVisibility)
                .GetComponentInChildren<TextMeshProUGUI>(true);
            _texturesLabel = AddActionButton(template, _section.transform, "", 220f, ToggleTextures)
                .GetComponentInChildren<TextMeshProUGUI>(true);
            _swapLabel = AddActionButton(template, _section.transform, "", 220f, ToggleSwapYz)
                .GetComponentInChildren<TextMeshProUGUI>(true);
            _autoLoadLabel = AddActionButton(template, _section.transform, "", 220f, ToggleAutoLoad)
                .GetComponentInChildren<TextMeshProUGUI>(true);
        }

        // ------------------------------------------------------------------
        // Mesh manager popup window
        // ------------------------------------------------------------------

        private void OpenMeshWindow()
        {
            if (_root == null)
            {
                SetStatus("Load a model first");
                return;
            }
            if (_meshWindow == null) BuildMeshWindow();
            if (_meshWindow == null) return;
            _meshWindow.SetActive(true);
            _meshWindow.transform.SetAsLastSibling();
            RebuildMeshRows();
        }

        private void BuildMeshWindow()
        {
            if (_rootCanvas == null || _templateButton == null)
            {
                SetStatus("Mesh manager UI is unavailable");
                return;
            }

            _meshWindow = new GameObject("ReferenceMeshWindow");
            _meshWindow.transform.SetParent(_rootCanvas.transform, false);
            RectTransform windowRect = _meshWindow.AddComponent<RectTransform>();
            windowRect.sizeDelta = new Vector2(480f, 560f);
            windowRect.anchoredPosition = new Vector2(220f, 0f);
            Image windowImage = _meshWindow.AddComponent<Image>();
            windowImage.color = new Color(0.07f, 0.09f, 0.13f, 0.97f);
            VerticalLayoutGroup layout = _meshWindow.AddComponent<VerticalLayoutGroup>();
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            layout.spacing = 6f;
            layout.padding = new RectOffset(10, 10, 10, 10);

            GameObject titleRow = MakeRow("MeshWindowTitleRow", _meshWindow.transform, 32f);
            Image titleImage = titleRow.AddComponent<Image>();
            titleImage.color = new Color(1f, 1f, 1f, 0.03f);
            WindowDragHandle drag = titleRow.AddComponent<WindowDragHandle>();
            drag.Target = windowRect;
            _meshWindowTitle = MakeText("MESH MANAGER", titleRow.transform, _fontSize * 0.78f, 30f);
            _meshWindowTitle.alignment = TextAlignmentOptions.Left;
            _meshWindowTitle.raycastTarget = false;
            AddActionButton(_templateButton, titleRow.transform, "✕", 36f, () => _meshWindow.SetActive(false));

            GameObject filterRow = MakeRow("MeshWindowFilterRow", _meshWindow.transform, 30f);
            TextMeshProUGUI filterCaption = MakeText("Filter", filterRow.transform, _fontSize * 0.64f, 28f);
            filterCaption.alignment = TextAlignmentOptions.Left;
            filterCaption.gameObject.GetComponent<LayoutElement>().preferredWidth = 60f;
            filterCaption.gameObject.GetComponent<LayoutElement>().flexibleWidth = 0f;
            _meshFilterInput = MakeTextInput(_templateButton, filterRow.transform, "",
                TMP_InputField.ContentType.Standard, _ => ApplyMeshFilter(), null);

            GameObject bulkRow = MakeRow("MeshWindowBulkRow", _meshWindow.transform, 30f);
            AddActionButton(_templateButton, bulkRow.transform, "Show All", 100f, () => SetAllMeshesActive(true));
            AddActionButton(_templateButton, bulkRow.transform, "Hide All", 100f, () => SetAllMeshesActive(false));
            AddActionButton(_templateButton, bulkRow.transform, "Reset Pieces", 110f, ResetPieceTransforms);

            GameObject scrollObject = new GameObject("MeshWindowScroll");
            scrollObject.transform.SetParent(_meshWindow.transform, false);
            scrollObject.AddComponent<RectTransform>();
            LayoutElement scrollElement = scrollObject.AddComponent<LayoutElement>();
            scrollElement.flexibleHeight = 1f;
            scrollElement.preferredHeight = 420f;
            Image scrollImage = scrollObject.AddComponent<Image>();
            scrollImage.color = new Color(0f, 0f, 0f, 0.25f);
            ScrollRect scroll = scrollObject.AddComponent<ScrollRect>();

            GameObject viewport = new GameObject("Viewport");
            viewport.transform.SetParent(scrollObject.transform, false);
            RectTransform viewportRect = viewport.AddComponent<RectTransform>();
            viewportRect.anchorMin = Vector2.zero;
            viewportRect.anchorMax = Vector2.one;
            viewportRect.offsetMin = new Vector2(4f, 4f);
            viewportRect.offsetMax = new Vector2(-4f, -4f);
            viewport.AddComponent<RectMask2D>();
            Image viewportImage = viewport.AddComponent<Image>();
            viewportImage.color = new Color(0f, 0f, 0f, 0.01f);

            GameObject content = new GameObject("Content");
            content.transform.SetParent(viewport.transform, false);
            RectTransform contentRect = content.AddComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.offsetMin = Vector2.zero;
            contentRect.offsetMax = Vector2.zero;
            VerticalLayoutGroup contentLayout = content.AddComponent<VerticalLayoutGroup>();
            contentLayout.childForceExpandWidth = true;
            contentLayout.childForceExpandHeight = false;
            contentLayout.spacing = 2f;
            content.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _meshRowsParent = content.transform;

            scroll.content = contentRect;
            scroll.viewport = viewportRect;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.scrollSensitivity = 30f;
        }

        private void RebuildMeshRows()
        {
            if (_meshRowsParent == null) return;
            foreach (MeshRow row in _meshRows)
                if (row.RowObject != null) Destroy(row.RowObject);
            _meshRows.Clear();

            foreach (GameObject meshObject in _meshObjects)
            {
                if (meshObject == null) continue;
                GameObject captured = meshObject;

                GameObject rowObject = MakeRow("MeshRow", _meshRowsParent, 26f);
                Image rowImage = rowObject.AddComponent<Image>();
                rowImage.color = new Color(0f, 0f, 0f, 0f);
                RowHoverHandler hover = rowObject.AddComponent<RowHoverHandler>();
                hover.HoverChanged = hovered => SetMeshHoverHighlight(captured, hovered);
                GameObject nameButton = AddActionButton(_templateButton, rowObject.transform, "", 220f,
                    () => ToggleMesh(captured));
                LayoutElement nameElement = nameButton.GetComponent<LayoutElement>();
                nameElement.flexibleWidth = 1f;
                nameElement.preferredHeight = 24f;
                TextMeshProUGUI nameLabel = nameButton.GetComponentInChildren<TextMeshProUGUI>(true);
                if (nameLabel != null)
                {
                    nameLabel.alignment = TextAlignmentOptions.Left;
                    nameLabel.fontSizeMax = Mathf.Max(9f, _fontSize * 0.62f);
                }

                GameObject moveButton = AddActionButton(_templateButton, rowObject.transform, "Move", 52f,
                    () => StartEdit(captured.transform));
                moveButton.GetComponent<LayoutElement>().preferredHeight = 24f;
                GameObject deleteButton = AddActionButton(_templateButton, rowObject.transform, "✕", 30f,
                    () => DeleteMesh(captured));
                deleteButton.GetComponent<LayoutElement>().preferredHeight = 24f;

                _meshRows.Add(new MeshRow { RowObject = rowObject, Mesh = captured, Label = nameLabel });
            }
            RefreshMeshRows();
            ApplyMeshFilter();
        }

        private void RefreshMeshRows()
        {
            int alive = 0;
            foreach (MeshRow row in _meshRows)
            {
                if (row.Mesh == null || row.RowObject == null) continue;
                alive++;
                if (row.Label != null)
                {
                    row.Label.text = (row.Mesh.activeSelf ? "● " : "○ ") + row.Mesh.name;
                    row.Label.color = row.Mesh.activeSelf ? Color.white : new Color(1f, 1f, 1f, 0.45f);
                }
            }
            if (_meshWindowTitle != null) _meshWindowTitle.text = "MESH MANAGER (" + alive + ")";
        }

        private void ApplyMeshFilter()
        {
            string filter = _meshFilterInput != null ? _meshFilterInput.text.Trim() : "";
            foreach (MeshRow row in _meshRows)
            {
                if (row.RowObject == null) continue;
                bool visible = row.Mesh != null &&
                               (filter.Length == 0 ||
                                row.Mesh.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
                row.RowObject.SetActive(visible);
            }
        }

        private void ToggleMesh(GameObject meshObject)
        {
            if (meshObject == null) return;
            meshObject.SetActive(!meshObject.activeSelf);
            RefreshMeshRows();
            SaveModelState();
        }

        private void SetAllMeshesActive(bool active)
        {
            foreach (GameObject meshObject in _meshObjects)
                if (meshObject != null) meshObject.SetActive(active);
            RefreshMeshRows();
            SaveModelState();
        }

        private void ResetPieceTransforms()
        {
            foreach (GameObject meshObject in _meshObjects)
            {
                if (meshObject == null) continue;
                meshObject.transform.localPosition = Vector3.zero;
                meshObject.transform.localRotation = Quaternion.identity;
            }
            SaveModelState();
        }

        // Hover highlight: a duplicate of the piece's mesh rendered as a
        // front-culled unlit yellow shell, slightly inflated about the mesh
        // bounds so it reads as an outline (like the native part selection).
        private void SetMeshHoverHighlight(GameObject meshObject, bool hovered)
        {
            if (!hovered)
            {
                if (meshObject == null || _hoverTarget == meshObject) ClearHoverOutline();
                return;
            }
            if (meshObject == null || _root == null)
            {
                ClearHoverOutline();
                return;
            }
            if (_hoverTarget == meshObject && _hoverOutline != null) return;
            ClearHoverOutline();

            MeshFilter filter = meshObject.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) return;
            Mesh mesh = filter.sharedMesh;
            EnsureOutlineMaterial();

            _hoverOutline = new GameObject("ReferenceHoverOutline") { hideFlags = HideFlags.DontSave };
            _hoverOutline.transform.SetParent(meshObject.transform.parent, false);
            _hoverOutline.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = _hoverOutline.AddComponent<MeshRenderer>();
            Material[] materials = new Material[mesh.subMeshCount];
            for (int i = 0; i < materials.Length; i++) materials[i] = _outlineMaterial;
            renderer.sharedMaterials = materials;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            // Constant world-space thickness: grow each axis by ~2% of the
            // largest extent, so thin pieces (wings, panels) still get a rim.
            Bounds bounds = mesh.bounds;
            float thickness = Mathf.Max(bounds.extents.x, bounds.extents.y, bounds.extents.z) * 0.02f;
            Vector3 inflate = new Vector3(
                1f + thickness / Mathf.Max(bounds.extents.x, 1e-5f),
                1f + thickness / Mathf.Max(bounds.extents.y, 1e-5f),
                1f + thickness / Mathf.Max(bounds.extents.z, 1e-5f));

            OutlineFollower follower = _hoverOutline.AddComponent<OutlineFollower>();
            follower.Target = meshObject.transform;
            follower.BoundsCenter = bounds.center;
            follower.Inflate = inflate;
            _hoverTarget = meshObject;
        }

        private void ClearHoverOutline()
        {
            if (_hoverOutline != null) Destroy(_hoverOutline);
            _hoverOutline = null;
            _hoverTarget = null;
        }

        private void EnsureOutlineMaterial()
        {
            if (_outlineMaterial != null) return;
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ??
                            Shader.Find("Unlit/Color") ??
                            Shader.Find("Universal Render Pipeline/Lit");
            _outlineMaterial = new Material(shader) { name = "ReferenceHoverOutline", hideFlags = HideFlags.DontSave };
            Color highlight = new Color(1f, 0.76f, 0.12f, 1f);
            if (_outlineMaterial.HasProperty("_BaseColor")) _outlineMaterial.SetColor("_BaseColor", highlight);
            if (_outlineMaterial.HasProperty("_Color")) _outlineMaterial.SetColor("_Color", highlight);
            if (_outlineMaterial.HasProperty("_Cull")) _outlineMaterial.SetFloat("_Cull", (float)CullMode.Front);
        }

        private void DeleteMesh(GameObject meshObject)
        {
            if (meshObject == null) return;
            if (_hoverTarget == meshObject) ClearHoverOutline();
            if (_editingModel && _editTarget == meshObject.transform) EndModelEdit();
            if (!_deletedNames.Contains(meshObject.name)) _deletedNames.Add(meshObject.name);
            foreach (MeshFilter filter in meshObject.GetComponentsInChildren<MeshFilter>(true))
                if (filter.sharedMesh != null) Destroy(filter.sharedMesh);
            foreach (MeshRenderer renderer in meshObject.GetComponentsInChildren<MeshRenderer>(true))
                _texturedMaterials.Remove(renderer);
            _meshObjects.Remove(meshObject);
            Destroy(meshObject);
            for (int i = _meshRows.Count - 1; i >= 0; i--)
            {
                if (_meshRows[i].Mesh != meshObject) continue;
                if (_meshRows[i].RowObject != null) Destroy(_meshRows[i].RowObject);
                _meshRows.RemoveAt(i);
            }
            RefreshMeshRows();
            SaveModelState();
        }

        // ------------------------------------------------------------------
        // Panel actions
        // ------------------------------------------------------------------

        private void TogglePanel()
        {
            _panelVisible = !_panelVisible;
            if (_section != null) _section.SetActive(_panelVisible);
            if (_panelVisible) RefreshLabels();
        }

        private void OpenObjFromDisk()
        {
            string path = PickObjFile();
            if (!string.IsNullOrEmpty(path)) LoadModel(path);
        }

        // Native Win32 file picker on a dedicated STA thread (Ookii is bundled with the game).
        private string PickObjFile()
        {
            string result = null;
            Thread thread = new Thread(delegate ()
            {
                try
                {
                    VistaOpenFileDialog dialog = new VistaOpenFileDialog
                    {
                        Title = "Select an OBJ reference model",
                        Filter = "OBJ models (*.obj)|*.obj|All files (*.*)|*.*",
                        Multiselect = false,
                        CheckFileExists = true
                    };
                    string initial = File.Exists(_modelPath.Value)
                        ? Path.GetDirectoryName(_modelPath.Value)
                        : ModelDirectory;
                    if (Directory.Exists(initial)) dialog.InitialDirectory = initial;
                    if (dialog.ShowDialog() == WinForms.DialogResult.OK) result = dialog.FileName;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("File picker failed: " + ex.Message);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();
            return result;
        }

        private void ReloadModel()
        {
            if (File.Exists(_modelPath.Value))
            {
                LoadModel(_modelPath.Value);
                return;
            }
            string first = Directory.GetFiles(ModelDirectory, "*.obj", SearchOption.AllDirectories).FirstOrDefault();
            if (first == null) SetStatus("No OBJ found; use Browse...");
            else LoadModel(first);
        }

        private void ToggleModelVisibility()
        {
            if (_root == null) return;
            _modelVisible = !_modelVisible;
            _root.SetActive(_modelVisible);
            RefreshLabels();
            SaveModelState();
        }

        private void ToggleTextures()
        {
            _showTextures.Value = !_showTextures.Value;
            Config.Save();
            ApplyTextureMode();
            RefreshLabels();
        }

        private void ToggleSwapYz()
        {
            _swapYz.Value = !_swapYz.Value;
            Config.Save();
            SetStatus("Axis conversion changed; Reload to apply");
            RefreshLabels();
        }

        private void ToggleAutoLoad()
        {
            _autoLoad.Value = !_autoLoad.Value;
            Config.Save();
            RefreshLabels();
        }

        private void SetScaleValue(string value)
        {
            if (TryParsePositiveFloat(value, out float parsed))
            {
                _scale.Value = parsed;
                Config.Save();
                ApplyScale();
            }
            if (_scaleInput != null)
                _scaleInput.SetTextWithoutNotify(FormatNumber(_scale.Value));
            RefreshLabels();
            SaveModelState();
        }

        private void ResetTransform()
        {
            if (_root == null) return;
            _root.transform.localPosition = Vector3.zero;
            _root.transform.localEulerAngles = Vector3.zero;
            _scale.Value = 1f;
            Config.Save();
            ApplyScale();
            if (_scaleInput != null) _scaleInput.SetTextWithoutNotify(FormatNumber(_scale.Value));
            RefreshLabels();
            SaveModelState();
        }

        private void RefreshLabels()
        {
            if (_statusLabel != null) _statusLabel.text = _status;
            if (_pathLabel != null)
                _pathLabel.text = File.Exists(_modelPath.Value) ? Path.GetFileName(_modelPath.Value) : "Models folder: " + ModelDirectory;
            if (_editLabel != null)
                _editLabel.text = _editingModel
                    ? "Editing '" + (_editTarget != null ? _editTarget.name : "?") + "'..."
                    : "Move / Rotate Model (gizmos)";
            if (_meshManagerLabel != null)
                _meshManagerLabel.text = "Mesh Manager (" + _meshObjects.Count(m => m != null) + " pieces)...";
            if (_visibilityLabel != null)
                _visibilityLabel.text = _root == null ? "Model: not loaded" : "Model visible: " + (_modelVisible ? "ON" : "OFF");
            if (_texturesLabel != null)
                _texturesLabel.text = "Textures: " + (_showTextures.Value ? "ON" : "OFF (plain white)");
            if (_swapLabel != null) _swapLabel.text = "Swap Y/Z: " + (_swapYz.Value ? "ON" : "OFF") + " (reload after change)";
            if (_autoLoadLabel != null) _autoLoadLabel.text = "Auto-load: " + (_autoLoad.Value ? "ON" : "OFF");
            RefreshMeshRows();
        }

        // ------------------------------------------------------------------
        // Session state (root pose, scale, per-piece visibility/pose, deletes)
        // ------------------------------------------------------------------

        private static string StateFileNameFor(string modelPath)
        {
            string full = Path.GetFullPath(modelPath);
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(full.ToLowerInvariant()));
                StringBuilder hex = new StringBuilder(8);
                for (int i = 0; i < 4; i++) hex.Append(hash[i].ToString("x2"));
                return Path.GetFileNameWithoutExtension(full) + "_" + hex + ".xml";
            }
        }

        private static string Num(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static float ReadFloat(XElement element, string name, float fallback)
        {
            XAttribute attribute = element.Attribute(name);
            return attribute != null &&
                   float.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                ? value
                : fallback;
        }

        private static object[] PoseAttributes(Transform transform)
        {
            Vector3 p = transform.localPosition;
            Quaternion q = transform.localRotation;
            return new object[]
            {
                new XAttribute("px", Num(p.x)), new XAttribute("py", Num(p.y)), new XAttribute("pz", Num(p.z)),
                new XAttribute("qx", Num(q.x)), new XAttribute("qy", Num(q.y)),
                new XAttribute("qz", Num(q.z)), new XAttribute("qw", Num(q.w))
            };
        }

        private static void ApplyPose(Transform transform, XElement element)
        {
            transform.localPosition = new Vector3(
                ReadFloat(element, "px", transform.localPosition.x),
                ReadFloat(element, "py", transform.localPosition.y),
                ReadFloat(element, "pz", transform.localPosition.z));
            Quaternion q = new Quaternion(
                ReadFloat(element, "qx", 0f), ReadFloat(element, "qy", 0f),
                ReadFloat(element, "qz", 0f), ReadFloat(element, "qw", 1f));
            if (Mathf.Abs(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w - 1f) < 0.1f)
                transform.localRotation = q;
        }

        private void SaveModelState()
        {
            if (_restoringState || _root == null || string.IsNullOrEmpty(_loadedModelPath)) return;
            try
            {
                XElement xml = new XElement("ReferenceModelState",
                    new XAttribute("path", _loadedModelPath),
                    new XAttribute("visible", _modelVisible),
                    new XAttribute("scale", Num(_scale.Value)),
                    PoseAttributes(_root.transform));
                foreach (GameObject meshObject in _meshObjects)
                {
                    if (meshObject == null) continue;
                    XElement piece = new XElement("Piece",
                        new XAttribute("name", meshObject.name),
                        new XAttribute("active", meshObject.activeSelf));
                    piece.Add(PoseAttributes(meshObject.transform));
                    xml.Add(piece);
                }
                foreach (string name in _deletedNames)
                    xml.Add(new XElement("Deleted", new XAttribute("name", name)));
                Directory.CreateDirectory(StateDirectory);
                xml.Save(Path.Combine(StateDirectory, StateFileNameFor(_loadedModelPath)));
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Could not save reference model state: " + ex.Message);
            }
        }

        private bool RestoreModelState(string modelPath)
        {
            string file;
            try
            {
                file = Path.Combine(StateDirectory, StateFileNameFor(modelPath));
                if (!File.Exists(file)) return false;
            }
            catch
            {
                return false;
            }

            try
            {
                XElement xml = XElement.Load(file);
                _restoringState = true;
                try
                {
                    ApplyPose(_root.transform, xml);
                    float scale = ReadFloat(xml, "scale", _scale.Value);
                    if (scale > 0f && !float.IsNaN(scale) && !float.IsInfinity(scale))
                    {
                        _scale.Value = scale;
                        Config.Save();
                        ApplyScale();
                    }
                    XAttribute visible = xml.Attribute("visible");
                    if (visible != null && bool.TryParse(visible.Value, out bool isVisible))
                    {
                        _modelVisible = isVisible;
                        _root.SetActive(_modelVisible);
                    }

                    Dictionary<string, GameObject> byName =
                        new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
                    foreach (GameObject meshObject in _meshObjects)
                        if (meshObject != null && !byName.ContainsKey(meshObject.name))
                            byName[meshObject.name] = meshObject;

                    foreach (XElement piece in xml.Elements("Piece"))
                    {
                        string name = (string)piece.Attribute("name");
                        if (name == null || !byName.TryGetValue(name, out GameObject meshObject) || meshObject == null)
                            continue;
                        XAttribute active = piece.Attribute("active");
                        if (active != null && bool.TryParse(active.Value, out bool isActive))
                            meshObject.SetActive(isActive);
                        ApplyPose(meshObject.transform, piece);
                    }
                    foreach (XElement deleted in xml.Elements("Deleted"))
                    {
                        string name = (string)deleted.Attribute("name");
                        if (name != null && byName.TryGetValue(name, out GameObject meshObject) && meshObject != null)
                            DeleteMesh(meshObject);
                    }
                }
                finally
                {
                    _restoringState = false;
                }
                if (_scaleInput != null) _scaleInput.SetTextWithoutNotify(FormatNumber(_scale.Value));
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Could not restore reference model state: " + ex.Message);
                return false;
            }
        }

        // ------------------------------------------------------------------
        // Model loading
        // ------------------------------------------------------------------

        private void LoadModel(string path)
        {
            try
            {
                path = Path.GetFullPath(Environment.ExpandEnvironmentVariables((path ?? "").Trim().Trim('"')));
                if (!File.Exists(path)) throw new FileNotFoundException("OBJ not found", path);
                SetStatus("Loading " + Path.GetFileName(path) + " ...");
                UnloadModel(false);
                RuntimeObjLoader loader = new RuntimeObjLoader(Logger, _swapYz.Value);
                _root = loader.Load(path);
                _root.name = "SP2 Reference - " + Path.GetFileNameWithoutExtension(path);
                _root.transform.position = Vector3.zero;
                _root.transform.rotation = Quaternion.identity;
                ApplyScale();
                _meshObjects.AddRange(loader.MeshObjects);
                foreach (MeshRenderer renderer in _root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    Material[] materials = renderer.sharedMaterials;
                    _texturedMaterials[renderer] = materials;
                    foreach (Material material in materials)
                    {
                        if (material == null || _ownedResources.Contains(material)) continue;
                        _ownedResources.Add(material);
                        if (material.HasProperty("_BaseMap") && material.GetTexture("_BaseMap") != null &&
                            !_ownedResources.Contains(material.GetTexture("_BaseMap")))
                            _ownedResources.Add(material.GetTexture("_BaseMap"));
                        if (material.HasProperty("_MainTex") && material.GetTexture("_MainTex") != null &&
                            !_ownedResources.Contains(material.GetTexture("_MainTex")))
                            _ownedResources.Add(material.GetTexture("_MainTex"));
                    }
                }
                ApplyTextureMode();
                _modelVisible = true;
                _modelPath.Value = path;
                Config.Save();
                _loadedModelPath = path;
                bool restored = RestoreModelState(path);
                SetStatus("Loaded " + _meshObjects.Count(m => m != null) + " meshes, " +
                          loader.VertexCount.ToString("N0", CultureInfo.InvariantCulture) + " vertices" +
                          (restored ? "; session restored" : ""));
                Logger.LogInfo(_status + " from " + path);
                if (_meshWindow != null && _meshWindow.activeSelf) RebuildMeshRows();
            }
            catch (Exception ex)
            {
                SetStatus("Load failed: " + ex.Message);
                Logger.LogError("Reference model load failed: " + ex);
            }
        }

        private void ApplyScale()
        {
            if (_root != null) _root.transform.localScale = Vector3.one * _scale.Value;
        }

        private void ApplyTextureMode()
        {
            if (_root == null) return;
            foreach (KeyValuePair<MeshRenderer, Material[]> pair in _texturedMaterials)
            {
                if (pair.Key == null) continue;
                if (_showTextures.Value)
                {
                    pair.Key.sharedMaterials = pair.Value;
                }
                else
                {
                    EnsureWhiteMaterial();
                    Material[] materials = new Material[pair.Value.Length];
                    for (int i = 0; i < materials.Length; i++) materials[i] = _whiteMaterial;
                    pair.Key.sharedMaterials = materials;
                }
            }
        }

        private void EnsureWhiteMaterial()
        {
            if (_whiteMaterial != null) return;
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Unlit/Texture");
            _whiteMaterial = new Material(shader) { name = "ReferenceModelWhite", hideFlags = HideFlags.DontSave };
            if (_whiteMaterial.HasProperty("_BaseColor")) _whiteMaterial.SetColor("_BaseColor", Color.white);
            if (_whiteMaterial.HasProperty("_Color")) _whiteMaterial.SetColor("_Color", Color.white);
            if (_whiteMaterial.HasProperty("_Cull")) _whiteMaterial.SetFloat("_Cull", 0f);
        }

        private void UnloadModel(bool updateStatus = true)
        {
            SaveModelState();
            EndModelEdit();
            ClearHoverOutline();
            if (_root != null)
            {
                foreach (MeshFilter filter in _root.GetComponentsInChildren<MeshFilter>(true))
                    if (filter.sharedMesh != null) Destroy(filter.sharedMesh);
                foreach (UnityEngine.Object resource in _ownedResources)
                    if (resource != null) Destroy(resource);
                Destroy(_root);
            }
            _root = null;
            _loadedModelPath = null;
            _deletedNames.Clear();
            _ownedResources.Clear();
            _texturedMaterials.Clear();
            _meshObjects.Clear();
            foreach (MeshRow row in _meshRows)
                if (row.RowObject != null) Destroy(row.RowObject);
            _meshRows.Clear();
            if (_meshWindow != null) _meshWindow.SetActive(false);
            if (updateStatus) SetStatus("No model loaded");
            else RefreshLabels();
        }

        private void SetStatus(string message)
        {
            _status = message ?? "";
            RefreshLabels();
        }

        // ------------------------------------------------------------------
        // Native-styled widget helpers
        // ------------------------------------------------------------------

        private static bool TryParsePositiveFloat(string value, out float parsed)
        {
            bool ok = float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) ||
                      float.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out parsed);
            if (!ok || parsed <= 0f || float.IsNaN(parsed) || float.IsInfinity(parsed))
            {
                parsed = 0f;
                return false;
            }
            parsed = Mathf.Clamp(parsed, 0.0001f, 10000f);
            return true;
        }

        private static string FormatNumber(float value)
        {
            return value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        private TMP_InputField MakeTextInput(Button template, Transform parent, string initialText,
            TMP_InputField.ContentType contentType,
            UnityEngine.Events.UnityAction<string> onValueChanged,
            UnityEngine.Events.UnityAction<string> onEndEdit)
        {
            GameObject fieldObject = new GameObject("ReferenceTextInput");
            fieldObject.transform.SetParent(parent, false);
            RectTransform rect = fieldObject.AddComponent<RectTransform>();
            fieldObject.AddComponent<CanvasRenderer>();
            Image background = fieldObject.AddComponent<Image>();
            Image templateImage = template.GetComponent<Image>();
            if (templateImage != null)
            {
                background.sprite = templateImage.sprite;
                background.type = templateImage.type;
                background.color = templateImage.color;
                background.material = templateImage.material;
            }

            GameObject textObject = new GameObject("Text");
            textObject.transform.SetParent(fieldObject.transform, false);
            RectTransform textRect = textObject.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(6f, 2f);
            textRect.offsetMax = new Vector2(-6f, -2f);
            TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
            text.alignment = TextAlignmentOptions.Center;
            text.fontSize = _fontSize * 0.75f;
            text.color = Color.white;
            text.enableAutoSizing = true;
            text.fontSizeMin = 7f;
            text.fontSizeMax = Mathf.Max(10f, _fontSize);
            if (_font != null)
            {
                text.font = _font;
                text.fontSharedMaterial = _fontMaterial;
            }

            TMP_InputField input = fieldObject.AddComponent<TMP_InputField>();
            input.targetGraphic = background;
            input.textViewport = rect;
            input.textComponent = text;
            input.contentType = contentType;
            input.lineType = TMP_InputField.LineType.SingleLine;
            input.text = initialText;
            if (onValueChanged != null) input.onValueChanged.AddListener(onValueChanged);
            if (onEndEdit != null) input.onEndEdit.AddListener(onEndEdit);

            LayoutElement element = fieldObject.AddComponent<LayoutElement>();
            element.preferredWidth = 100f;
            element.minWidth = 70f;
            element.preferredHeight = 30f;
            element.flexibleWidth = 1f;
            return input;
        }

        private GameObject AddActionButton(Button template, Transform parent, string text, float width, UnityEngine.Events.UnityAction action)
        {
            GameObject buttonObject = CloneNativeButton(template, "ReferenceAction", text, parent);
            LayoutElement element = buttonObject.GetComponent<LayoutElement>() ?? buttonObject.AddComponent<LayoutElement>();
            element.preferredWidth = width;
            element.minWidth = Mathf.Min(40f, width);
            element.preferredHeight = 30f;
            element.flexibleWidth = _section != null && parent == _section.transform ? 1f : 0f;
            Button button = buttonObject.GetComponent<Button>();
            button.onClick.RemoveAllListeners();
            button.onClick.AddListener(action);
            return buttonObject;
        }

        private GameObject CloneNativeButton(Button source, string name, string label, Transform parent)
        {
            GameObject clone = Instantiate(source.gameObject, parent);
            clone.name = name;
            clone.SetActive(true);
            Button button = clone.GetComponent<Button>();
            if (button != null) button.interactable = true;
            CanvasGroup group = clone.GetComponent<CanvasGroup>();
            if (group != null)
            {
                group.alpha = 1f;
                group.interactable = true;
                group.blocksRaycasts = true;
            }
            TextMeshProUGUI text = clone.GetComponentInChildren<TextMeshProUGUI>(true);
            if (text != null)
            {
                text.text = label;
                text.enabled = true;
                text.textWrappingMode = TextWrappingModes.NoWrap;
                text.enableAutoSizing = true;
                text.fontSizeMin = 7f;
                text.fontSizeMax = Mathf.Max(10f, text.fontSize);
                if (_font != null)
                {
                    text.font = _font;
                    text.fontSharedMaterial = _fontMaterial;
                }
            }
            return clone;
        }

        private TextMeshProUGUI MakeText(string value, Transform parent, float size, float height)
        {
            GameObject textObject = new GameObject("ReferenceText");
            textObject.transform.SetParent(parent, false);
            textObject.AddComponent<RectTransform>();
            TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
            text.text = value;
            text.fontSize = size;
            text.color = Color.white;
            text.raycastTarget = false;
            if (_font != null)
            {
                text.font = _font;
                text.fontSharedMaterial = _fontMaterial;
            }
            LayoutElement element = textObject.AddComponent<LayoutElement>();
            element.preferredHeight = height;
            element.flexibleWidth = 1f;
            return text;
        }

        private static GameObject MakeRow(string name, Transform parent, float height)
        {
            GameObject row = new GameObject(name);
            row.transform.SetParent(parent, false);
            row.AddComponent<RectTransform>();
            HorizontalLayoutGroup layout = row.AddComponent<HorizontalLayoutGroup>();
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;
            layout.spacing = 4f;
            LayoutElement element = row.AddComponent<LayoutElement>();
            element.preferredHeight = height;
            element.flexibleWidth = 1f;
            return row;
        }

        private void CaptureNativeTextStyle(GameObject flyout, Button template)
        {
            TextMeshProUGUI label = template.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label == null) label = flyout.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label == null) return;
            _font = label.font;
            _fontMaterial = label.fontSharedMaterial;
            _fontSize = label.fontSize > 0f ? label.fontSize : 14f;
        }

        private static Button FindButtonTemplate(GameObject flyout)
        {
            Button[] buttons = flyout.GetComponentsInChildren<Button>(true);
            for (int i = 0; i < buttons.Length; i++)
            {
                if (buttons[i] == null || buttons[i].name == "ReferenceModelMenuButton" || buttons[i].name == "ReferenceAction") continue;
                TextMeshProUGUI label = buttons[i].GetComponentInChildren<TextMeshProUGUI>(true);
                if (label != null &&
                    (string.Equals(label.text.Trim(), "Designer Tutorials", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(label.text.Trim(), "Undo History", StringComparison.OrdinalIgnoreCase)))
                    return buttons[i];
            }
            return null;
        }

        private static GameObject FindMainMenuFlyout()
        {
            TextMeshProUGUI[] labels = UnityEngine.Object.FindObjectsByType<TextMeshProUGUI>(FindObjectsSortMode.None);
            for (int i = 0; i < labels.Length; i++)
            {
                TextMeshProUGUI label = labels[i];
                if (label == null || !string.Equals(label.text.Trim(), "Upload Craft", StringComparison.OrdinalIgnoreCase))
                    continue;
                Transform current = label.transform;
                GameObject best = null;
                for (int depth = 0; current != null && depth < 12; depth++, current = current.parent)
                {
                    if (current.name.IndexOf("flyout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        current.name.IndexOf("main-menu", StringComparison.OrdinalIgnoreCase) >= 0)
                        return current.gameObject;
                    RectTransform rect = current as RectTransform;
                    if (rect != null && rect.rect.width >= 250f && current.GetComponentsInChildren<Button>(true).Length >= 8)
                        best = current.gameObject;
                    if (current.GetComponent<Canvas>() != null) break;
                }
                if (best != null) return best;
            }
            return null;
        }

        private static void DestroyExistingInjectedUi(GameObject flyout)
        {
            Transform[] transforms = flyout.GetComponentsInChildren<Transform>(true);
            for (int i = transforms.Length - 1; i >= 0; i--)
            {
                if (transforms[i] != null &&
                    (transforms[i].name == "ReferenceModelMenuButton" || transforms[i].name == "ReferenceModelSection"))
                    UnityEngine.Object.Destroy(transforms[i].gameObject);
            }
        }

        private void ResetInjectedUi()
        {
            _injected = false;
            _panelVisible = false;
            _section = null;
            _toggleButton = null;
            _toggleLabel = null;
            _statusLabel = null;
            _pathLabel = null;
            _editLabel = null;
            _visibilityLabel = null;
            _texturesLabel = null;
            _swapLabel = null;
            _autoLoadLabel = null;
            _meshManagerLabel = null;
            _scaleInput = null;
            _templateButton = null;
            _rootCanvas = null;
            if (_meshWindow != null) Destroy(_meshWindow);
            _meshWindow = null;
            _meshRowsParent = null;
            _meshWindowTitle = null;
            _meshFilterInput = null;
            _meshRows.Clear();
        }
    }
}
