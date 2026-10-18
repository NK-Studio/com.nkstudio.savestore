using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MemoryPack;
using SaveStore.Storage;
using UnityEditor;
using UnityEngine;

namespace SaveStore.Editor
{
    /// <summary>
    /// 세이브 뷰어 (메뉴 Tools/Save Store/Save Viewer). 세이브 폴더를 <b>고치지 않고</b> 읽어 manifest·세대·커밋 시각과
    /// 도메인별 내용을 JSON 으로 보여 준다. 바이너리 세이브의 가독성 문제를 도구로 푼다.
    /// 암호화·서명 세이브를 보려면 프로젝트 에디터 코드에서 <see cref="ProjectOptionsProvider"/> 에 같은 SaveOptions 를 돌려주게 등록한다.
    /// </summary>
    public sealed class SaveViewerWindow : EditorWindow
    {
        private enum TransformChoice
        {
            None,
            Deflate,
            Project,
        }

        /// <summary>
        /// 프로젝트가 쓰는 SaveOptions(변환 포함). 예: [InitializeOnLoadMethod] 에서 <c>SaveViewerWindow.ProjectOptionsProvider = () =&gt; MySave.CreateOptions();</c>
        /// </summary>
        public static Func<SaveOptions> ProjectOptionsProvider;

        private string _folder;
        private TransformChoice _transformChoice = TransformChoice.None;
        private SaveManifest _manifest;
        private string _status = "폴더를 고르고 [읽기]를 누르세요.";
        private readonly List<DomainView> _domainViews = new List<DomainView>();
        private Vector2 _scroll;

        private sealed class DomainView
        {
            public SaveManifestEntry Entry;
            public string RecordTypeName;
            public string Json;
            public bool IsExpanded;
        }

        [MenuItem("Tools/Save Store/Save Viewer")]
        public static void Open()
        {
            GetWindow<SaveViewerWindow>("Save Viewer");
        }

        private void OnEnable()
        {
            if (string.IsNullOrEmpty(_folder) == true)
            {
                _folder = Application.persistentDataPath;
            }
        }

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                _folder = EditorGUILayout.TextField("세이브 폴더", _folder);
                if (GUILayout.Button("찾기", GUILayout.Width(50)))
                {
                    string picked = EditorUtility.OpenFolderPanel("세이브 폴더 (manifest.bin 이 있는 폴더)", _folder, string.Empty);
                    if (string.IsNullOrEmpty(picked) == false)
                    {
                        _folder = picked;
                    }
                }

                if (GUILayout.Button("열기", GUILayout.Width(50)) && Directory.Exists(_folder) == true)
                {
                    EditorUtility.RevealInFinder(_folder);
                }
            }

            _transformChoice = (TransformChoice)EditorGUILayout.EnumPopup("변환", _transformChoice);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("읽기"))
                {
                    Read();
                }

                using (new EditorGUI.DisabledScope(_domainViews.Count == 0))
                {
                    if (GUILayout.Button("JSON 내보내기"))
                    {
                        ExportJson();
                    }
                }
            }

            EditorGUILayout.HelpBox(_status, MessageType.None);
            if (_manifest != null)
            {
                DateTime? committedAt = _manifest.CommittedAtUnixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(_manifest.CommittedAtUnixMs).LocalDateTime : (DateTime?)null;
                EditorGUILayout.LabelField("커밋 세대", _manifest.Generation.ToString());
                EditorGUILayout.LabelField("커밋 시각", committedAt.HasValue == true ? committedAt.Value.ToString("yyyy-MM-dd HH:mm:ss") : "(기록 없음)");
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (DomainView view in _domainViews)
            {
                string title = $"{view.Entry.Key}   세대 {view.Entry.FileGeneration} · 스키마 {view.Entry.SchemaVersion} · {view.Entry.PayloadLength:N0} B · {view.RecordTypeName}";
                view.IsExpanded = EditorGUILayout.Foldout(view.IsExpanded, title, true);
                if (view.IsExpanded == true)
                {
                    EditorGUILayout.TextArea(view.Json, GUILayout.ExpandHeight(false));
                }
            }

            EditorGUILayout.EndScrollView();
        }

        private void Read()
        {
            _domainViews.Clear();
            _manifest = null;
            if (Directory.Exists(_folder) == false)
            {
                _status = "폴더가 없습니다.";
                return;
            }

            SaveOptions options = CreateOptions(out string optionsError);
            if (options == null)
            {
                _status = optionsError;
                return;
            }

            var repository = new SaveRepository(new FileSaveStorage(_folder), options);
            _manifest = repository.PeekLatestManifest();
            if (_manifest == null)
            {
                _status = "읽을 수 있는 manifest 가 없습니다(세이브 폴더가 아니거나, 변환 설정이 다르거나, 손상됐습니다).";
                return;
            }

            Dictionary<string, Type> recordTypes = SaveRecordTypes.BuildKeyToRecordTypeMap();
            foreach (SaveManifestEntry entry in _manifest.Entries)
            {
                _domainViews.Add(BuildView(repository, entry, recordTypes));
            }

            _status = $"도메인 {_domainViews.Count}개를 읽었습니다.";
        }

        private static DomainView BuildView(SaveRepository repository, SaveManifestEntry entry, Dictionary<string, Type> recordTypes)
        {
            var view = new DomainView { Entry = entry, RecordTypeName = "(타입 모름)" };
            DomainFile file = repository.ReadDomain(entry);
            if (file.Status == DomainFileStatus.Corrupted)
            {
                view.Json = "손상: " + file.FailureReason;
                return view;
            }

            if (recordTypes.TryGetValue(entry.Key, out Type recordType) == false)
            {
                view.Json = "이 키의 SaveDomain<T> 를 찾지 못했습니다(생성자 인자로 키를 받는 도메인이거나 이 프로젝트에 없는 도메인).";
                return view;
            }

            view.RecordTypeName = recordType.Name;
            try
            {
                object record = MemoryPackSerializer.Deserialize(recordType, file.Payload);
                view.Json = SaveRecordDumper.ToJson(record);
            }
            catch (Exception exception)
            {
                view.Json = "역직렬화 실패: " + exception.Message;
            }

            return view;
        }

        private SaveOptions CreateOptions(out string error)
        {
            error = null;
            switch (_transformChoice)
            {
                case TransformChoice.None:
                    return new SaveOptions();
                case TransformChoice.Deflate:
                    return new SaveOptions { PayloadTransform = new DeflateCompressionTransform() };
                default:
                    if (ProjectOptionsProvider == null)
                    {
                        error = "SaveViewerWindow.ProjectOptionsProvider 가 등록되지 않았습니다.";
                        return null;
                    }

                    return ProjectOptionsProvider();
            }
        }

        private void ExportJson()
        {
            string path = EditorUtility.SaveFilePanel("세이브 JSON 내보내기", _folder, "save-dump", "json");
            if (string.IsNullOrEmpty(path) == true)
            {
                return;
            }

            var builder = new StringBuilder("{\n");
            for (int i = 0; i < _domainViews.Count; i++)
            {
                DomainView view = _domainViews[i];
                builder.Append("  \"").Append(view.Entry.Key).Append("\": ");
                builder.Append(view.Json.StartsWith("{") == true || view.Json.StartsWith("[") == true ? view.Json.Replace("\n", "\n  ") : "\"" + view.Json.Replace("\"", "'") + "\"");
                builder.Append(i < _domainViews.Count - 1 ? ",\n" : "\n");
            }

            builder.Append('}');
            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
            _status = $"내보냈습니다: {path}";
        }
    }
}
