using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace SpriteFusion
{
    public class SpriteFusionWindow : EditorWindow
    {
        const string OutputDir = "Assets/SpriteFusion/Generated";
        const int AnimationOutputFrames = 8;
        const int AnimationColors = 24;
        static readonly int[] GenerationSizes = { 128, 112, 96, 80, 64, 48, 32, 24, 16 };
        static readonly string[] GenerationSizeLabels = { "128x128", "112x112", "96x96", "80x80", "64x64", "48x48", "32x32", "24x24", "16x16" };

        Session _session;
        AssetRegistry _registry;

        string _pendingDeviceCode = "";
        string _pendingVerificationUrl = "";
        string _pendingUserCode = "";
        bool _pollCancelled;

        string _prompt = "";
        int _selectedGenerationSize = 64;
        string _animationPrompt = "";
        int _selectedSourceIndex;
        [NonSerialized] bool _isGenerating;
        [NonSerialized] bool _isAnimating;
        string _status = "";
        bool _showPricing;
        Vector2 _scroll;

        [MenuItem("Window/Sprite Fusion")]
        public static void Open()
        {
            var window = GetWindow<SpriteFusionWindow>("Sprite Fusion");
            window.minSize = new Vector2(260, 400);
        }

        void OnEnable()
        {
            _session = Session.Load();
            _registry = AssetRegistry.Load();
        }

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            if (!string.IsNullOrEmpty(_status))
                EditorGUILayout.HelpBox(_status, MessageType.Info);
            if (_showPricing && GUILayout.Button("View plans"))
                Application.OpenURL(Api.BaseUrl + "/pixel-art-generator#pricing");

            if (_session.IsConnected) DrawConnected();
            else if (!string.IsNullOrEmpty(_pendingDeviceCode)) DrawConnecting();
            else DrawDisconnected();

            EditorGUILayout.EndScrollView();
        }

        void DrawDisconnected()
        {
            EditorGUILayout.LabelField("Connect your Sprite Fusion account to generate pixel art.",
                EditorStyles.wordWrappedLabel);
            if (GUILayout.Button("Connect"))
                _ = StartDeviceFlow();
        }

        void DrawConnecting()
        {
            EditorGUILayout.LabelField("Approve this code in your browser.", EditorStyles.wordWrappedLabel);
            var big = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 24,
                alignment = TextAnchor.MiddleCenter
            };
            EditorGUILayout.LabelField(_pendingUserCode, big, GUILayout.Height(36));

            if (GUILayout.Button("Open connection page") && !string.IsNullOrEmpty(_pendingVerificationUrl))
                Application.OpenURL(_pendingVerificationUrl);
            if (GUILayout.Button("Cancel"))
                CancelDeviceFlow();
        }

        void DrawConnected()
        {
            EditorGUILayout.LabelField(
                $"Connected as {(string.IsNullOrEmpty(_session.email) ? "Sprite Fusion user" : _session.email)}",
                EditorStyles.wordWrappedLabel);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Generate", EditorStyles.boldLabel);
            _prompt = EditorGUILayout.TextArea(_prompt, GUILayout.MinHeight(60));
            _selectedGenerationSize = GenerationSizes[Mathf.Clamp(
                EditorGUILayout.Popup("Size", Array.IndexOf(GenerationSizes, _selectedGenerationSize), GenerationSizeLabels),
                0,
                GenerationSizes.Length - 1)];
            using (new EditorGUI.DisabledScope(_isGenerating))
                if (GUILayout.Button(_isGenerating ? "Generating..." : "Generate"))
                    _ = HandleGenerate();

            EditorGUILayout.Space(16);
            EditorGUILayout.LabelField("Animate", EditorStyles.boldLabel);

            var imageEntries = _registry.ImageEntries();
            if (imageEntries.Count == 0)
            {
                using (new EditorGUI.DisabledScope(true))
                    EditorGUILayout.Popup(0, new[] { "Generate an image first" });
            }
            else
            {
                var labels = imageEntries.Select((e, i) => $"{e.DisplayLabel()} - {i + 1:00}").ToArray();
                _selectedSourceIndex = Mathf.Clamp(_selectedSourceIndex, 0, imageEntries.Count - 1);
                _selectedSourceIndex = EditorGUILayout.Popup("Source image", _selectedSourceIndex, labels);

                var previewTex = AssetDatabase.LoadAssetAtPath<Texture2D>(
                    imageEntries[_selectedSourceIndex].assetPath);
                if (previewTex != null)
                {
                    var rect = GUILayoutUtility.GetRect(100, 100, GUILayout.ExpandWidth(true));
                    EditorGUI.DrawTextureTransparent(rect, previewTex, ScaleMode.ScaleToFit);
                }
            }

            _animationPrompt = EditorGUILayout.TextArea(_animationPrompt, GUILayout.MinHeight(60));
            using (new EditorGUI.DisabledScope(_isAnimating || imageEntries.Count == 0))
                if (GUILayout.Button(_isAnimating ? "Animating..." : "Animate"))
                    _ = HandleAnimate(imageEntries);

            EditorGUILayout.Space(8);
            if (GUILayout.Button("Logout"))
                Logout();
        }

        void SetStatus(string message, bool showPricing = false)
        {
            _status = message;
            _showPricing = showPricing;
            Repaint();
        }

        async Task StartDeviceFlow()
        {
            SetStatus("Creating connection code...");
            var response = await Api.PostJson("/api/integrations/auth/device/start?integration=unity", "{}", null, Api.JsonTimeout);
            if (!response.Ok)
            {
                SetStatus("Unable to start connection.");
                return;
            }

            var data = JsonUtility.FromJson<DeviceStartResponse>(response.Body);
            if (data == null || string.IsNullOrEmpty(data.deviceCode))
            {
                SetStatus("Unable to start connection.");
                return;
            }

            _pendingDeviceCode = data.deviceCode;
            _pendingVerificationUrl = data.verificationUriComplete ?? "";
            _pendingUserCode = data.userCode ?? "";
            _pollCancelled = false;
            SetStatus("Approve this code in your browser.");
            _ = PollDeviceToken(Mathf.Max(1, data.interval));
        }

        async Task PollDeviceToken(int intervalSeconds)
        {
            while (!_pollCancelled && !string.IsNullOrEmpty(_pendingDeviceCode))
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds));
                if (_pollCancelled || string.IsNullOrEmpty(_pendingDeviceCode)) return;

                var payload = $"{{\"deviceCode\":\"{_pendingDeviceCode}\"}}";
                var response = await Api.PostJson("/api/integrations/auth/device/token", payload, null, Api.JsonTimeout);

                if (response.Status == 0)
                {
                    SetStatus("Connection check failed. Retrying...");
                    continue;
                }

                if (response.Ok)
                {
                    var tokens = JsonUtility.FromJson<TokenResponse>(response.Body);
                    _session.Apply(tokens);
                    ClearDeviceFlow();
                    SetStatus("");
                    return;
                }

                var error = JsonUtility.FromJson<DeviceErrorResponse>(response.Body)?.error ?? "authorization_pending";
                switch (error)
                {
                    case "authorization_pending":
                        SetStatus("Waiting for approval...");
                        break;
                    case "slow_down":
                        intervalSeconds += 5;
                        SetStatus("Waiting before polling again...");
                        break;
                    case "expired_token":
                        CancelDeviceFlow();
                        SetStatus("Connection code expired.");
                        return;
                    case "access_denied":
                        CancelDeviceFlow();
                        SetStatus("Connection cancelled.");
                        return;
                    default:
                        CancelDeviceFlow();
                        SetStatus("Unable to connect.");
                        return;
                }
            }
        }

        void CancelDeviceFlow()
        {
            _pollCancelled = true;
            ClearDeviceFlow();
            Repaint();
        }

        void ClearDeviceFlow()
        {
            _pendingDeviceCode = "";
            _pendingVerificationUrl = "";
            _pendingUserCode = "";
        }

        void Logout()
        {
            _session = new Session();
            Session.Delete();
            Repaint();
        }

        async Task<bool> EnsureFreshSession()
        {
            if (!_session.IsExpiring) return true;
            return await RefreshSession();
        }

        async Task<bool> RefreshSession()
        {
            if (string.IsNullOrEmpty(_session.refresh_token))
            {
                Logout();
                return false;
            }

            var payload = $"{{\"refresh_token\":\"{_session.refresh_token}\"}}";
            var response = await Api.PostJson("/api/integrations/auth/refresh", payload, null, Api.JsonTimeout);
            if (!response.Ok)
            {
                Logout();
                SetStatus("Session expired. Connect again.");
                return false;
            }

            _session.Apply(JsonUtility.FromJson<TokenResponse>(response.Body));
            return true;
        }

        async Task HandleGenerate()
        {
            var prompt = (_prompt ?? "").Trim();
            if (prompt == "")
            {
                SetStatus("Prompt is required.");
                return;
            }

            _isGenerating = true;
            SetStatus("");
            try
            {
                if (!await EnsureFreshSession()) return;

                var payload = JsonUtility.ToJson(new ImagePayload { operation = "generate", prompt = prompt, size = _selectedGenerationSize });
                var response = await Api.PostJson("/api/v1/generate", payload,
                    _session.access_token, Api.GenerationTimeout);

                if (response.Status == 401 && await RefreshSession())
                    response = await Api.PostJson("/api/v1/generate", payload,
                        _session.access_token, Api.GenerationTimeout);

                if (response.Status == 0)
                {
                    SetStatus("Generation request failed.");
                    return;
                }
                if (!response.Ok)
                {
                    SetGenerationError(response.Body, response.Status);
                    return;
                }

                var events = Api.ParseEvents(response.Body);
                var lastEvent = events.Count > 0 ? events[events.Count - 1] : new GenerationEvent();
                if (lastEvent.type != "completed" || lastEvent.status != "succeeded")
                {
                    SetGenerationError(lastEvent);
                    return;
                }

                var assets = new List<GeneratedAsset>();
                var assetIds = new HashSet<string>();
                foreach (var generationEvent in events)
                {
                    if (generationEvent.type != "output") continue;
                    var asset = generationEvent.asset;
                    if (asset == null || string.IsNullOrEmpty(asset.id)) continue;
                    if (assetIds.Add(asset.id)) assets.Add(asset);
                }
                if (assets.Count == 0)
                {
                    SetStatus("Generation returned no image.");
                    return;
                }
                // In batch order: sprites stream as they are ready, and each image asset's batchIndex is its place in the batch.
                assets = assets.Select((asset, arrival) => (asset, arrival))
                    .OrderBy(entry => entry.asset.batchIndex >= 0 ? entry.asset.batchIndex : entry.arrival)
                    .Select(entry => entry.asset)
                    .ToList();

                var downloadable = assets
                    .Where(a => a != null && !string.IsNullOrEmpty(a.assetUrl)).ToList();
                if (downloadable.Count == 0)
                {
                    SetStatus("Generated images have no download URL.");
                    return;
                }

                UnityEngine.Object firstSaved = null;
                for (var i = 0; i < downloadable.Count; i++)
                {
                    var download = await Api.GetBytes(downloadable[i].assetUrl, null, Api.DownloadTimeout);
                    if (!download.Ok)
                    {
                        SetStatus("Unable to download generated image.");
                        return;
                    }

                    var assetPath = GetOutputPath(prompt, "png", "", i);
                    SavePngAsPixelArtSprite(download.Bytes, assetPath, SpriteImportMode.Single, 0, 0, 0);
                    _registry.Register(assetPath, downloadable[i], "image");
                    if (firstSaved == null)
                        firstSaved = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                }

                _registry.Save();
                if (firstSaved != null) EditorGUIUtility.PingObject(firstSaved);
                SetStatus("");
            }
            finally
            {
                _isGenerating = false;
                Repaint();
            }
        }

        async Task HandleAnimate(List<AssetRegistry.Entry> imageEntries)
        {
            if (imageEntries.Count == 0)
            {
                SetStatus("Generate an image first.");
                return;
            }

            var source = imageEntries[Mathf.Clamp(_selectedSourceIndex, 0, imageEntries.Count - 1)];
            if (string.IsNullOrEmpty(source.assetId))
            {
                SetStatus("Source image is missing its Sprite Fusion asset ID.");
                return;
            }

            var prompt = (_animationPrompt ?? "").Trim();
            if (prompt == "")
            {
                SetStatus("Animation prompt is required.");
                return;
            }

            _isAnimating = true;
            SetStatus("");
            try
            {
                if (!await EnsureFreshSession()) return;

                var payload = JsonUtility.ToJson(new AnimationPayload
                {
                    operation = "animate",
                    prompt = prompt,
                    inputs = new[] { new GenerationInput { asset_id = source.assetId } },
                    output_frames = AnimationOutputFrames,
                    colors = AnimationColors,
                });

                var response = await Api.PostJson("/api/v1/generate", payload,
                    _session.access_token, Api.GenerationTimeout);
                if (response.Status == 401 && await RefreshSession())
                    response = await Api.PostJson("/api/v1/generate", payload,
                        _session.access_token, Api.GenerationTimeout);

                if (response.Status == 0)
                {
                    SetStatus("Animation request failed.");
                    return;
                }
                if (!response.Ok)
                {
                    SetGenerationError(response.Body, response.Status);
                    return;
                }

                var events = Api.ParseEvents(response.Body);
                var lastEvent = events.Count > 0 ? events[events.Count - 1] : new GenerationEvent();
                if (lastEvent.type != "completed" || lastEvent.status != "succeeded")
                {
                    SetGenerationError(lastEvent);
                    return;
                }

                var asset = events
                    .Where(generationEvent => generationEvent.type == "output")
                    .Select(generationEvent => generationEvent.asset)
                    .LastOrDefault(generatedAsset => generatedAsset != null);
                if (asset == null || string.IsNullOrEmpty(asset.id))
                {
                    SetStatus("Animation returned no asset.");
                    return;
                }

                if (string.IsNullOrEmpty(asset.spritesheetUrl))
                {
                    SetStatus("Animation returned no spritesheet URL.");
                    return;
                }

                var download = await Api.GetBytes(asset.spritesheetUrl, null, Api.DownloadTimeout);
                if (!download.Ok)
                {
                    SetStatus("Unable to download animation spritesheet.");
                    return;
                }

                if (asset.width <= 0 || asset.height <= 0 || asset.frameCount <= 0)
                {
                    SetStatus("Animation metadata is invalid.");
                    return;
                }

                var sheetPath = GetOutputPath(prompt, "png", "spritesheet");
                SavePngAsPixelArtSprite(download.Bytes, sheetPath, SpriteImportMode.Multiple,
                    asset.width, asset.height, asset.frameCount);

                var clipPath = Path.ChangeExtension(sheetPath, ".anim");
                var clip = CreateAnimationClip(sheetPath, clipPath,
                    asset.frameCount, asset.fps > 0 ? asset.fps : 8f);
                if (clip == null)
                {
                    SetStatus("Unable to create animation clip.");
                    return;
                }

                _registry.Register(sheetPath, asset, "animation", clipPath);
                _registry.Save();
                EditorGUIUtility.PingObject(clip);
                SetStatus("");
            }
            finally
            {
                _isAnimating = false;
                Repaint();
            }
        }

        static void SavePngAsPixelArtSprite(byte[] bytes, string assetPath,
            SpriteImportMode mode, int frameWidth, int frameHeight, int frameCount)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(assetPath)));
            File.WriteAllBytes(Path.GetFullPath(assetPath), bytes);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

            var importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = mode;
            importer.filterMode = FilterMode.Point;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.mipmapEnabled = false;

            if (mode == SpriteImportMode.Multiple && frameCount > 0)
            {
#pragma warning disable 0618
                var baseName = Path.GetFileNameWithoutExtension(assetPath);
                var metas = new SpriteMetaData[frameCount];
                for (var i = 0; i < frameCount; i++)
                {
                    metas[i] = new SpriteMetaData
                    {
                        name = $"{baseName}_{i}",
                        rect = new Rect(i * frameWidth, 0, frameWidth, frameHeight),
                        alignment = (int)SpriteAlignment.Center,
                    };
                }
                importer.spritesheet = metas;
#pragma warning restore 0618
            }

            importer.SaveAndReimport();
        }

        static AnimationClip CreateAnimationClip(string sheetPath, string clipPath,
            int frameCount, float fps)
        {
            var sprites = AssetDatabase.LoadAllAssetsAtPath(sheetPath)
                .OfType<Sprite>()
                .OrderBy(s => s.name, new NumericSuffixComparer())
                .ToArray();
            if (sprites.Length == 0) return null;

            var clip = new AnimationClip { frameRate = fps };
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            var binding = new EditorCurveBinding
            {
                type = typeof(SpriteRenderer),
                path = "",
                propertyName = "m_Sprite",
            };

            var keyframes = new ObjectReferenceKeyframe[sprites.Length];
            for (var i = 0; i < sprites.Length; i++)
                keyframes[i] = new ObjectReferenceKeyframe { time = i / fps, value = sprites[i] };

            AnimationUtility.SetObjectReferenceCurve(clip, binding, keyframes);
            AssetDatabase.CreateAsset(clip, clipPath);
            AssetDatabase.SaveAssets();
            return clip;
        }

        class NumericSuffixComparer : IComparer<string>
        {
            public int Compare(string a, string b)
            {
                var ia = ExtractSuffix(a);
                var ib = ExtractSuffix(b);
                return ia != ib ? ia.CompareTo(ib) : string.CompareOrdinal(a, b);
            }

            static int ExtractSuffix(string s)
            {
                var idx = s.LastIndexOf('_');
                return idx >= 0 && int.TryParse(s.Substring(idx + 1), out var n) ? n : 0;
            }
        }

        void SetGenerationError(GenerationEvent evt)
        {
            var message = string.IsNullOrEmpty(evt.error?.message) ? "Generation failed." : evt.error.message;
            SetStatus(message);
        }

        void SetGenerationError(string body, int statusCode)
        {
            var error = JsonUtility.FromJson<ErrorResponse>(string.IsNullOrEmpty(body) ? "{}" : body)
                        ?? new ErrorResponse();
            var errorMessage = error.error?.message ?? "";

            var needsPlan = statusCode == 402 &&
                errorMessage.StartsWith("An active subscription is required.", StringComparison.Ordinal);
            if (needsPlan)
            {
                SetStatus("You need an active plan", true);
                return;
            }

            var message = string.IsNullOrEmpty(errorMessage) ? "Generation failed." : errorMessage;
            SetStatus(message);
        }

        static string GetOutputPath(string prompt, string extension, string suffix = "", int index = -1)
        {
            var fileName = SafeFileName(prompt);
            if (fileName == "") fileName = "sprite";
            if (suffix != "") fileName = $"{fileName}_{suffix}";
            if (index >= 0) fileName = $"{fileName}_{index + 1}";
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return $"{OutputDir}/{fileName}_{timestamp}.{extension}";
        }

        static string SafeFileName(string value)
        {
            var output = new StringBuilder();
            foreach (var raw in value)
            {
                var c = char.ToLowerInvariant(raw);
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) output.Append(c);
                else if (output.Length > 0 && output[output.Length - 1] != '_') output.Append('_');
                if (output.Length >= 40) break;
            }
            return output.ToString().TrimEnd('_');
        }

        [Serializable]
        class ImagePayload
        {
            public string operation;
            public string prompt;
            public int size;
        }

        [Serializable]
        class GenerationInput
        {
            public string asset_id;
        }

        [Serializable]
        class AnimationPayload
        {
            public string operation;
            public string prompt;
            public GenerationInput[] inputs;
            public int output_frames;
            public int colors;
        }
    }

    [Serializable]
    public class AssetRegistry
    {
        [Serializable]
        public class Entry
        {
            public string assetPath;
            public string type;
            public string assetId;
            public string prompt;
            public int width;
            public int height;
            public int frameCount;
            public float fps;
            public string animationClipPath;
            public string savedAt;

            public string DisplayLabel()
            {
                var label = string.IsNullOrEmpty(prompt) ? "Generated image" : prompt.Trim();
                if (label.Length > 36) label = label.Substring(0, 33) + "...";
                return label;
            }
        }

        public List<Entry> entries = new List<Entry>();

        static string Dir => Path.Combine(
            Directory.GetParent(Application.dataPath).FullName, "UserSettings", "SpriteFusion");

        static string FilePath => Path.Combine(Dir, "assets.json");

        public static AssetRegistry Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonUtility.FromJson<AssetRegistry>(File.ReadAllText(FilePath))
                           ?? new AssetRegistry();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"SpriteFusion: could not load registry: {e.Message}");
            }

            return new AssetRegistry();
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            entries.RemoveAll(e => !File.Exists(Path.GetFullPath(e.assetPath)));
            File.WriteAllText(FilePath, JsonUtility.ToJson(this, true));
        }

        public void Register(string assetPath, GeneratedAsset asset, string type, string clipPath = "")
        {
            entries.RemoveAll(e => e.assetPath == assetPath);
            entries.Add(new Entry
            {
                assetPath = assetPath,
                type = type,
                assetId = asset.id ?? "",
                prompt = asset.prompt ?? "",
                width = asset.width,
                height = asset.height,
                frameCount = asset.frameCount,
                fps = asset.fps,
                animationClipPath = clipPath,
                savedAt = DateTime.UtcNow.ToString("o"),
            });
        }

        public List<Entry> ImageEntries() =>
            entries.Where(e => e.type == "image" && File.Exists(Path.GetFullPath(e.assetPath)))
                .OrderBy(e => e.assetPath)
                .ToList();
    }
}
