using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Newtonsoft.Json;

using UnityEditor;
using UnityEngine;

namespace BeastBlood.Editor.AssetGraph
{
    /// <summary>
    /// 프로젝트 전체 에셋의 정방향·역방향 참조 인덱스. 언리얼 Asset Registry의 참조 정보에 해당한다.
    /// </summary>
    /// <remarks>
    /// <para>YAML 텍스트 에셋에서 <c>guid: </c>/<c>GUID: </c> 뒤의 32자리 GUID를 뽑아 선을 만든다.
    /// <c>AssetDatabase.GetDependencies</c>는 Addressables <c>m_AssetGUID</c> 문자열 필드를 놓치므로 쓰지 않는다.</para>
    /// <para>코드에서 씬 이름 문자열로 여는 경우는 직렬화 참조가 없으므로, 빌드 설정 씬 이름이
    /// <c>Assets/</c> 아래(서드파티 제외) C# 문자열 리터럴로 등장하면 "코드 경유(추정)" 선으로 따로 기록한다.</para>
    /// <para>전체 갱신은 수십 초 걸리므로 파일 I/O를 스레드 풀에서 돌린다(<see cref="StartBuild"/>).
    /// 이후 변경분은 <see cref="BBAssetRefPostprocessor"/>가 <see cref="ApplyChanges"/>로 파일 단위 반영한다.</para>
    /// <para>결과는 <c>Library/BBAssetGraph/RefIndex.json</c>(SVN 비대상)에 캐시한다.</para>
    /// </remarks>
    public class BBAssetRefIndex
    {
        #region Fields

        private const int CACHE_VERSION = 1;
        private const string CACHE_PATH = "Library/BBAssetGraph/RefIndex.json";
        private const string SETTINGS_ROOT = "ProjectSettings";
        private const string BUILTIN_GUID_PREFIX = "0000000000000000";

        // 참조를 담을 수 있는 YAML 에셋 확장자
        private static readonly HashSet<string> s_sourceExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".prefab", ".asset", ".unity", ".controller", ".overrideController", ".anim", ".mat",
            ".playable", ".mixer", ".shadergraph", ".shadersubgraph", ".physicMaterial", ".spriteatlas",
            ".vfx", ".lighting", ".mask", ".guiskin", ".fontsettings", ".renderTexture", ".brush",
            ".terrainlayer", ".spriteatlasv2", ".preset", // 2026-09-15 누락 전수 조사로 추가
        };

        // 서드파티로 취급해 기본 결과에서 숨기는 폴더. Packages/는 패키지 매니저 설치분(Opsive, URP 등)
        private static readonly string[] s_thirdPartyRoots =
        {
            "Assets/20_Plugins/", "Assets/30_ThirdParty/", "Assets/Plugins/", "Packages/",
        };

        private static readonly string[] s_guidTokens = { "guid: ", "GUID: " };

        private static BBAssetRefIndex s_instance;

        /// <summary>
        /// 인덱스 내용이 바뀐 뒤(전체 갱신 완료, 파일 단위 반영) 호출된다. 그래프 창이 다시 그리는 신호로 쓴다.
        /// </summary>
        public static event Action Changed;

        // 조회용 파생 사전 (캐시에 저장하지 않음)
        private Dictionary<string, string> _pathToGuid = new Dictionary<string, string>();
        private Dictionary<string, List<FAssetRefLink>> _forward = new Dictionary<string, List<FAssetRefLink>>();
        private Dictionary<string, List<FAssetRefLink>> _reverse = new Dictionary<string, List<FAssetRefLink>>();

        #endregion Fields

        #region Properties

        /// <summary>
        /// 캐시에서 불러온(없으면 빈) 인덱스. 도메인 리로드 후에는 캐시에서 다시 읽는다.
        /// </summary>
        public static BBAssetRefIndex Instance => s_instance ??= LoadOrCreate();

        /// <summary>
        /// 캐시 파일이 있는지. 한 번도 만든 적 없으면 후처리기가 인덱스를 불러오지 않게 하는 용도.
        /// </summary>
        public static bool HasCache => s_instance != null || File.Exists(CACHE_PATH);

        public int Version { get; set; } = CACHE_VERSION;

        public DateTime BuiltAtUtc { get; set; }

        // 에셋 GUID → 에셋 경로 (Assets/... 또는 Packages/...)
        public Dictionary<string, string> GuidToPath { get; set; } = new Dictionary<string, string>();

        // 소스 에셋 경로 → 스캔 결과
        public Dictionary<string, BBAssetRefEntry> Entries { get; set; } = new Dictionary<string, BBAssetRefEntry>();

        // C# 파일 경로 → 문자열로 등장한 빌드 씬 GUID 목록
        public Dictionary<string, List<string>> CodeRefs { get; set; } = new Dictionary<string, List<string>>();

        [JsonIgnore]
        public bool IsBuilt => GuidToPath.Count > 0;

        [JsonIgnore]
        public bool IsBuilding { get; private set; }

        [JsonIgnore]
        public long LastBuildMs { get; private set; }

        [JsonIgnore]
        public int LastScannedCount { get; private set; }

        [JsonIgnore]
        public int LastReusedCount { get; private set; }

        [JsonIgnore]
        public string LastBuildError { get; private set; }

        // 마지막 전체 갱신 이후 후처리기가 파일 단위로 반영한 횟수
        [JsonIgnore]
        public int AppliedChangeCount { get; private set; }

        #endregion Properties

        #region Public API

        /// <summary>
        /// 백그라운드 전체 갱신을 시작한다. 이미 진행 중이면 false.
        /// </summary>
        /// <param name="full">true면 캐시를 무시하고 전부 다시 스캔한다</param>
        public bool StartBuild(bool full)
        {
            if (IsBuilding) return false;

            IsBuilding = true;
            LastBuildError = null;
            _ = BuildAsync(full);
            return true;
        }

        /// <summary>
        /// 임포트·삭제·이동된 에셋만 인덱스에 반영하고 저장한다. 인덱스가 없거나 전체 갱신 중이면 무시한다.
        /// </summary>
        /// <param name="changedPaths">새로 생겼거나 내용이 바뀐 에셋 경로 (이동 후 경로 포함)</param>
        /// <param name="removedPaths">삭제된 에셋 경로 (이동 전 경로 포함)</param>
        public void ApplyChanges(IEnumerable<string> changedPaths, IEnumerable<string> removedPaths)
        {
            if (!IsBuilt || IsBuilding) return;

            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            var dirty = false;

            foreach (var path in removedPaths)
            {
                if (_pathToGuid.TryGetValue(path, out var guid))
                {
                    GuidToPath.Remove(guid);
                }

                dirty |= Entries.Remove(path) | CodeRefs.Remove(path);
            }

            Dictionary<string, string> sceneNameToGuid = null;
            foreach (var path in changedPaths)
            {
                if (!path.StartsWith("Assets/", StringComparison.Ordinal)) continue;

                var guid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guid)) continue;

                GuidToPath[guid] = path;
                dirty = true;

                var fullPath = Path.Combine(projectRoot, path);
                if (s_sourceExtensions.Contains(Path.GetExtension(path)))
                {
                    var info = new FileInfo(fullPath);
                    if (info.Exists)
                    {
                        Entries[path] = ScanYaml(fullPath, guid, info.LastWriteTimeUtc.Ticks, info.Length);
                        ResolvePackageGuids(GuidToPath, Entries[path].Refs);
                    }
                }
                else if (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !IsThirdParty(path) && File.Exists(fullPath))
                {
                    sceneNameToGuid ??= MapSceneNames(EditorBuildSettings.scenes.Select(scene => scene.path), GuidToPath);

                    var hits = ScanCode(fullPath, sceneNameToGuid);
                    if (hits != null)
                    {
                        CodeRefs[path] = hits;
                    }
                    else
                    {
                        CodeRefs.Remove(path);
                    }
                }
            }

            if (!dirty) return;

            RebuildLookups();
            Save();
            AppliedChangeCount++;
            Changed?.Invoke();
        }

        /// <summary>
        /// 지정한 파일만 수정 시각과 상관없이 다시 읽어 반영한다. 그래프 창의 '새로고침'용.
        /// </summary>
        /// <param name="assetPaths">다시 읽을 에셋 경로 (Assets/...). 디스크에 없으면 삭제로 처리한다</param>
        /// <returns>인덱스가 없거나 전체 갱신 중이라 반영하지 못했으면 false</returns>
        public bool RefreshFiles(IEnumerable<string> assetPaths)
        {
            if (!IsBuilt || IsBuilding) return false;

            var projectRoot = Directory.GetParent(Application.dataPath).FullName;
            var existing = new List<string>();
            var removed = new List<string>();
            foreach (var path in assetPaths)
            {
                (File.Exists(Path.Combine(projectRoot, path)) ? existing : removed).Add(path);
            }

            ApplyChanges(existing, removed);
            return true;
        }

        /// <summary>
        /// GUID·경로·파일 이름 중 무엇이든 받아 GUID로 바꾼다. 이름이 여러 에셋과 겹치면 null과 후보 목록을 준다.
        /// </summary>
        /// <param name="target">32자리 GUID, <c>Assets/...</c> 경로, 또는 파일 이름(확장자 생략 가능)</param>
        /// <param name="candidates">이름 검색으로 찾은 후보 경로가 채워진다</param>
        public string ResolveGuid(string target, List<string> candidates)
        {
            if (string.IsNullOrWhiteSpace(target)) return null;

            target = target.Trim().Replace('\\', '/');
            if (GuidToPath.ContainsKey(target)) return target;
            if (_pathToGuid.TryGetValue(target, out var byPath)) return byPath;

            foreach (var path in GuidToPath.Values)
            {
                var fileName = Path.GetFileName(path);
                if (string.Equals(fileName, target, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileNameWithoutExtension(fileName), target, StringComparison.OrdinalIgnoreCase))
                {
                    candidates.Add(path);
                }
            }

            return candidates.Count == 1 ? _pathToGuid[candidates[0]] : null;
        }

        /// <summary>
        /// GUID에 해당하는 경로. 프로젝트에 없으면 null.
        /// </summary>
        public string GetPath(string guid)
        {
            return guid != null && GuidToPath.TryGetValue(guid, out var path) ? path : null;
        }

        /// <summary>
        /// 시작 에셋에서 참조 그래프를 너비 우선으로 따라간다.
        /// </summary>
        /// <param name="rootGuid">시작 에셋 GUID</param>
        /// <param name="reverse">true면 "나를 쓰는 에셋"(역참조), false면 "내가 쓰는 에셋"(정참조)</param>
        /// <param name="maxDepth">최대 거리</param>
        /// <param name="includeThirdParty">false면 서드파티 폴더 에셋은 결과에서 빼고 더 따라가지도 않는다</param>
        public List<FAssetRefHit> Walk(string rootGuid, bool reverse, int maxDepth, bool includeThirdParty)
        {
            var map = reverse ? _reverse : _forward;
            var visited = new HashSet<string> { rootGuid };
            var result = new List<FAssetRefHit>();
            var frontier = new List<string> { rootGuid };

            for (var depth = 1; depth <= maxDepth && frontier.Count > 0; depth++)
            {
                var next = new List<string>();
                foreach (var guid in frontier)
                {
                    if (!map.TryGetValue(guid, out var links)) continue;

                    foreach (var link in links)
                    {
                        if (!visited.Add(link.Guid)) continue;

                        var path = GetPath(link.Guid);
                        if (!includeThirdParty && path != null && IsThirdParty(path)) continue;

                        result.Add(new FAssetRefHit(link.Guid, path, depth, link.ViaCode, guid));
                        next.Add(link.Guid);
                    }
                }

                frontier = next;
            }

            return result;
        }

        /// <summary>
        /// 프로젝트에 없는 GUID를 가리키는 참조(끊긴 참조) 목록. (소스 경로, 없는 GUID) 쌍.
        /// </summary>
        /// <param name="folder">이 폴더 아래 소스만 본다. null이면 전체</param>
        /// <param name="includeThirdParty">false면 서드파티 폴더 소스는 뺀다</param>
        public List<KeyValuePair<string, string>> FindMissing(string folder, bool includeThirdParty)
        {
            var result = new List<KeyValuePair<string, string>>();
            foreach (var pair in Entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!IsInScope(pair.Key, folder, includeThirdParty)) continue;

                foreach (var guid in pair.Value.Refs)
                {
                    if (!GuidToPath.ContainsKey(guid))
                    {
                        result.Add(new KeyValuePair<string, string>(pair.Key, guid));
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 어떤 에셋·코드 문자열에서도 참조되지 않는 에셋 경로 목록.
        /// </summary>
        /// <param name="folder">이 폴더 아래 에셋만 본다</param>
        /// <param name="extension">지정하면 이 확장자만 본다 (예: <c>.prefab</c>)</param>
        public List<string> FindUnreferenced(string folder, string extension)
        {
            var buildScenes = new HashSet<string>(EditorBuildSettings.scenes.Select(scene => scene.path));
            var result = new List<string>();

            foreach (var pair in GuidToPath)
            {
                var path = pair.Value;
                if (!IsInScope(path, folder, true)) continue;
                if (!Path.HasExtension(path) || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                if (extension != null && !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) continue;
                if (buildScenes.Contains(path)) continue;
                if (_reverse.ContainsKey(pair.Key)) continue;

                result.Add(path);
            }

            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>
        /// 서드파티 폴더(기본 결과에서 숨기는 폴더)에 속한 경로인지.
        /// </summary>
        public static bool IsThirdParty(string path)
        {
            foreach (var root in s_thirdPartyRoots)
            {
                if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        #endregion Public API

        #region Build

        /// <summary>
        /// 메인 스레드에서 입력을 모으고, 파일 I/O는 스레드 풀에서 돌린 뒤, 결과 반영은 다시 메인 스레드에서 한다.
        /// </summary>
        private async Task BuildAsync(bool full)
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                // 메인 스레드 전용 API는 여기서 미리 읽는다
                var projectRoot = Directory.GetParent(Application.dataPath).FullName;
                var dataPath = Application.dataPath;
                var buildScenePaths = EditorBuildSettings.scenes.Select(scene => scene.path).ToList();
                var oldEntries = full ? new Dictionary<string, BBAssetRefEntry>() : Entries;

                // 에디터 메인 스레드의 UnitySynchronizationContext 덕분에 await 뒤는 메인 스레드로 돌아온다
                var result = await Task.Run(() => ScanAll(projectRoot, dataPath, buildScenePaths, oldEntries));
                var scanMs = stopwatch.ElapsedMilliseconds;

                // 여기부터 메인 스레드
                var unknownCount = 0;
                foreach (var entry in result.Entries.Values)
                {
                    unknownCount += ResolvePackageGuids(result.GuidToPath, entry.Refs);
                }

                GuidToPath = result.GuidToPath;
                Entries = result.Entries;
                CodeRefs = result.CodeRefs;
                BuiltAtUtc = DateTime.UtcNow;
                RebuildLookups();
                Save();

                LastScannedCount = result.Scanned;
                LastReusedCount = result.Reused;
                LastBuildMs = stopwatch.ElapsedMilliseconds;
                AppliedChangeCount = 0;

                UnityEngine.Debug.Log(
                    $"[BBAssetRefIndex] 전체 갱신(full={full}) 스캔 {scanMs}ms(재스캔 {result.Scanned}, 재사용 {result.Reused}) / " +
                    $"패키지 GUID {unknownCount}건 / 반영·저장 {LastBuildMs - scanMs}ms / 합계 {LastBuildMs}ms");
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                LastBuildError = ex.Message;
                UnityEngine.Debug.LogException(ex);
            }
            finally
            {
                IsBuilding = false;
            }
        }

        /// <summary>
        /// 스레드 풀에서 도는 전체 스캔. Unity API를 호출하지 않는다.
        /// </summary>
        private static (Dictionary<string, string> GuidToPath, Dictionary<string, BBAssetRefEntry> Entries,
            Dictionary<string, List<string>> CodeRefs, int Scanned, int Reused)
            ScanAll(string projectRoot, string dataPath, List<string> buildScenePaths, Dictionary<string, BBAssetRefEntry> oldEntries)
        {
            // 1. .meta에서 GUID → 경로
            var guidToPath = new ConcurrentDictionary<string, string>();
            Parallel.ForEach(Directory.GetFiles(dataPath, "*.meta", SearchOption.AllDirectories), metaFile =>
            {
                var guid = ReadMetaGuid(metaFile);
                if (guid == null) return;

                guidToPath[guid] = ToAssetPath(projectRoot, metaFile.Substring(0, metaFile.Length - ".meta".Length));
            });

            // 2. YAML 소스 스캔 (수정 시각·크기가 같으면 이전 결과 재사용)
            var entries = new ConcurrentDictionary<string, BBAssetRefEntry>();
            var scanned = 0;
            var reused = 0;

            var sources = guidToPath.Where(pair => s_sourceExtensions.Contains(Path.GetExtension(pair.Value))).ToList();
            Parallel.ForEach(sources, pair =>
            {
                var info = new FileInfo(Path.Combine(projectRoot, pair.Value));
                if (!info.Exists) return;

                var ticks = info.LastWriteTimeUtc.Ticks;
                if (oldEntries.TryGetValue(pair.Value, out var old) && old.Ticks == ticks && old.Size == info.Length)
                {
                    entries[pair.Value] = old;
                    Interlocked.Increment(ref reused);
                    return;
                }

                entries[pair.Value] = ScanYaml(info.FullName, pair.Key, ticks, info.Length);
                Interlocked.Increment(ref scanned);
            });

            // 2-1. ProjectSettings (Preloaded Assets, 설정 오브젝트 등). .meta가 없으므로 경로 자체를 GUID 자리에 쓴다.
            //      에셋 임포트 대상이 아니라 후처리기로는 반영되지 않고 전체 갱신 때만 갱신된다
            var settingsRoot = Path.Combine(projectRoot, SETTINGS_ROOT);
            if (Directory.Exists(settingsRoot))
            {
                foreach (var file in Directory.GetFiles(settingsRoot, "*.asset"))
                {
                    var info = new FileInfo(file);
                    var settingsPath = ToAssetPath(projectRoot, file);
                    guidToPath[settingsPath] = settingsPath;
                    entries[settingsPath] = ScanYaml(file, settingsPath, info.LastWriteTimeUtc.Ticks, info.Length);
                    scanned++;
                }
            }

            // 3. 코드 경유: 빌드 씬 이름이 문자열 리터럴로 등장하는 C# 파일 (매번 다시 계산)
            var guidToPathMap = new Dictionary<string, string>(guidToPath);
            var sceneNameToGuid = MapSceneNames(buildScenePaths, guidToPathMap);
            var codeRefs = new ConcurrentDictionary<string, List<string>>();
            if (sceneNameToGuid.Count > 0)
            {
                Parallel.ForEach(Directory.GetFiles(dataPath, "*.cs", SearchOption.AllDirectories), file =>
                {
                    var assetPath = ToAssetPath(projectRoot, file);
                    if (IsThirdParty(assetPath)) return;

                    var hits = ScanCode(file, sceneNameToGuid);
                    if (hits != null)
                    {
                        codeRefs[assetPath] = hits;
                    }
                });
            }

            return (guidToPathMap, new Dictionary<string, BBAssetRefEntry>(entries),
                new Dictionary<string, List<string>>(codeRefs), scanned, reused);
        }

        #endregion Build

        #region Helper

        private static BBAssetRefIndex LoadOrCreate()
        {
            try
            {
                if (File.Exists(CACHE_PATH))
                {
                    var loaded = JsonConvert.DeserializeObject<BBAssetRefIndex>(File.ReadAllText(CACHE_PATH));
                    if (loaded != null && loaded.Version == CACHE_VERSION)
                    {
                        loaded.RebuildLookups();
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                // 캐시가 깨졌으면 빈 인덱스로 시작해 다음 갱신에서 다시 만든다
                UnityEngine.Debug.LogWarning($"[BBAssetRefIndex] 캐시를 읽지 못해 새로 만든다: {ex.Message}");
            }

            return new BBAssetRefIndex();
        }

        private void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CACHE_PATH));
            File.WriteAllText(CACHE_PATH, JsonConvert.SerializeObject(this));
        }

        private void RebuildLookups()
        {
            _pathToGuid = new Dictionary<string, string>(GuidToPath.Count);
            foreach (var pair in GuidToPath)
            {
                _pathToGuid[pair.Value] = pair.Key;
            }

            _forward = new Dictionary<string, List<FAssetRefLink>>();
            _reverse = new Dictionary<string, List<FAssetRefLink>>();

            foreach (var pair in Entries)
            {
                if (!_pathToGuid.TryGetValue(pair.Key, out var sourceGuid)) continue;

                foreach (var guid in pair.Value.Refs)
                {
                    AddLink(sourceGuid, guid, false);
                }
            }

            foreach (var pair in CodeRefs)
            {
                if (!_pathToGuid.TryGetValue(pair.Key, out var sourceGuid)) continue;

                foreach (var guid in pair.Value)
                {
                    AddLink(sourceGuid, guid, true);
                }
            }
        }

        private void AddLink(string fromGuid, string toGuid, bool viaCode)
        {
            if (!_forward.TryGetValue(fromGuid, out var forwardLinks))
            {
                forwardLinks = new List<FAssetRefLink>();
                _forward[fromGuid] = forwardLinks;
            }

            forwardLinks.Add(new FAssetRefLink(toGuid, viaCode));

            if (!_reverse.TryGetValue(toGuid, out var reverseLinks))
            {
                reverseLinks = new List<FAssetRefLink>();
                _reverse[toGuid] = reverseLinks;
            }

            reverseLinks.Add(new FAssetRefLink(fromGuid, viaCode));
        }

        /// <summary>
        /// Assets 밖(Packages, Library/PackageCache) GUID는 .meta를 직접 읽지 못하므로 AssetDatabase로 경로를 찾는다. 메인 스레드 전용.
        /// </summary>
        /// <returns>새로 경로를 찾은 GUID 수</returns>
        private static int ResolvePackageGuids(Dictionary<string, string> guidToPath, List<string> guids)
        {
            var resolved = 0;
            foreach (var guid in guids)
            {
                if (guidToPath.ContainsKey(guid)) continue;

                var packagePath = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(packagePath))
                {
                    guidToPath[guid] = packagePath;
                    resolved++;
                }
            }

            return resolved;
        }

        private static BBAssetRefEntry ScanYaml(string fullPath, string selfGuid, long ticks, long size)
        {
            var entry = new BBAssetRefEntry { Ticks = ticks, Size = size };
            var text = File.ReadAllText(fullPath);

            if (!text.StartsWith("%YAML", StringComparison.Ordinal))
            {
                entry.Binary = true;
                return entry;
            }

            var guids = new HashSet<string>();
            foreach (var token in s_guidTokens)
            {
                var index = 0;
                while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
                {
                    index += token.Length;
                    if (index + 32 > text.Length) break;

                    var guid = text.Substring(index, 32);
                    if (IsHexGuid(guid) && guid != selfGuid && !guid.StartsWith(BUILTIN_GUID_PREFIX, StringComparison.Ordinal))
                    {
                        guids.Add(guid);
                    }
                }
            }

            entry.Refs = guids.ToList();
            return entry;
        }

        private static List<string> ScanCode(string fullPath, Dictionary<string, string> sceneNameToGuid)
        {
            var text = File.ReadAllText(fullPath);
            List<string> hits = null;
            foreach (var pair in sceneNameToGuid)
            {
                if (text.IndexOf($"\"{pair.Key}\"", StringComparison.Ordinal) >= 0)
                {
                    (hits ??= new List<string>()).Add(pair.Value);
                }
            }

            return hits;
        }

        private static Dictionary<string, string> MapSceneNames(IEnumerable<string> scenePaths, Dictionary<string, string> guidToPath)
        {
            var pathToGuid = new Dictionary<string, string>();
            foreach (var pair in guidToPath)
            {
                pathToGuid[pair.Value] = pair.Key;
            }

            var result = new Dictionary<string, string>();
            foreach (var scenePath in scenePaths)
            {
                if (pathToGuid.TryGetValue(scenePath, out var guid))
                {
                    result[Path.GetFileNameWithoutExtension(scenePath)] = guid;
                }
            }

            return result;
        }

        private static string ReadMetaGuid(string metaFile)
        {
            using var reader = new StreamReader(metaFile);
            for (var i = 0; i < 4; i++)
            {
                var line = reader.ReadLine();
                if (line == null) break;

                if (line.StartsWith("guid: ", StringComparison.Ordinal) && line.Length >= 38)
                {
                    var guid = line.Substring(6, 32);
                    return IsHexGuid(guid) ? guid : null;
                }
            }

            return null;
        }

        private static bool IsHexGuid(string value)
        {
            if (value.Length != 32) return false;

            foreach (var c in value)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }

            return true;
        }

        private static bool IsInScope(string path, string folder, bool includeThirdParty)
        {
            if (!includeThirdParty && IsThirdParty(path)) return false;
            if (folder == null) return true;

            return path.StartsWith(folder.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static string ToAssetPath(string projectRoot, string fullPath)
        {
            return fullPath.Substring(projectRoot.Length + 1).Replace('\\', '/');
        }

        #endregion Helper
    }
}
