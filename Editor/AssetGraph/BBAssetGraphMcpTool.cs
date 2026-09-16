// ASSETGRAPH_MCP는 com.coplaydev.unity-mcp 패키지가 설치돼 있으면 asmdef versionDefines가 켠다
#if ASSETGRAPH_MCP
using System;
using System.Collections.Generic;
using System.Linq;

using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Tools;

using Newtonsoft.Json.Linq;

namespace BeastBlood.Editor.AssetGraph
{
    /// <summary>
    /// 에셋 참조 인덱스(<see cref="BBAssetRefIndex"/>)를 조회하는 MCP 커스텀 툴 <c>bb_asset_graph</c>.
    /// </summary>
    /// <remarks>
    /// 읽기 전용이다. 에셋을 수정하지 않고, 캐시는 <c>Library/</c>(SVN 비대상)에만 쓴다.
    /// </remarks>
    [McpForUnityTool(
        "bb_asset_graph",
        Description =
            "에셋 참조 그래프 조회(읽기 전용). action: " +
            "refs(대상을 쓰는 에셋, 역참조) | deps(대상이 쓰는 에셋, 정참조) | " +
            "unused(folder 아래 아무도 참조하지 않는 에셋) | missing(프로젝트에 없는 GUID를 가리키는 끊긴 참조) | " +
            "build(백그라운드 전체 갱신, full=true면 캐시 무시) | stats(갱신 진행 여부·규모). " +
            "평소 변경분은 에셋 임포트 시 자동 반영되므로 build는 처음 한 번이면 된다. " +
            "target은 GUID, Assets/ 경로, 파일 이름 모두 가능. " +
            "코드에서 문자열로 부르는 참조는 빌드 씬 이름만 추정(via=code)하며 그 외는 잡지 못한다. " +
            "상세는 https://github.com/w8err/Unity-AssetGraph README 참조.",
        Group = "core")]
    public static class BBAssetGraphMcpTool
    {
        #region Parameters

        /// <summary>
        /// MCP 서버에 노출할 파라미터 스키마.
        /// </summary>
        public class Parameters
        {
            [ToolParameter("수행할 작업. refs | deps | unused | missing | build | stats")]
            public string Action { get; set; }

            [ToolParameter("refs/deps 대상. GUID, Assets/ 경로, 또는 파일 이름(확장자 생략 가능).", Required = false)]
            public string Target { get; set; }

            [ToolParameter("refs/deps 탐색 깊이(기본 1, 최대 10).", Required = false)]
            public int? Depth { get; set; }

            [ToolParameter("서드파티 폴더(20_Plugins, 30_ThirdParty, Plugins) 포함 여부(기본 false).", Required = false)]
            public bool? IncludeThirdParty { get; set; }

            [ToolParameter("unused/missing 범위 폴더 (예: Assets/04_Prefabs).", Required = false)]
            public string Folder { get; set; }

            [ToolParameter("unused 전용. 확장자 필터 (예: .prefab).", Required = false)]
            public string Extension { get; set; }

            [ToolParameter("결과 최대 건수(기본 200).", Required = false)]
            public int? Limit { get; set; }

            [ToolParameter("build 전용. true면 캐시를 무시하고 전체 재스캔(기본 false).", Required = false)]
            public bool? Full { get; set; }
        }

        #endregion Parameters

        #region MCP Entry Point

        /// <summary>
        /// MCP 요청을 받아 참조 인덱스를 갱신·조회한다.
        /// </summary>
        /// <param name="params">MCP 요청 파라미터</param>
        public static object HandleCommand(JObject @params)
        {
            var action = ReadString(@params, "action");
            if (string.IsNullOrEmpty(action))
            {
                return new ErrorResponse($"'action'이 필요하다. 사용 가능: {string.Join(", ", ACTIONS)}");
            }

            action = action.Trim().ToLowerInvariant();
            if (Array.IndexOf(ACTIONS, action) < 0)
            {
                return new ErrorResponse($"알 수 없는 action '{action}'. 사용 가능: {string.Join(", ", ACTIONS)}");
            }

            try
            {
                var index = BBAssetRefIndex.Instance;
                if (action == "build")
                {
                    var started = index.StartBuild(ReadBool(@params, "full", false));
                    return new SuccessResponse(
                        started
                            ? "백그라운드 전체 갱신을 시작했다. 끝났는지는 stats의 building으로 확인해라."
                            : "이미 전체 갱신이 진행 중이다.",
                        BuildStats(index));
                }

                if (action == "stats")
                {
                    return new SuccessResponse("인덱스 상태.", BuildStats(index));
                }

                if (index.IsBuilding)
                {
                    return new ErrorResponse("전체 갱신 중이다. stats의 building이 false가 되면 다시 조회해라.", BuildStats(index));
                }

                if (!index.IsBuilt)
                {
                    index.StartBuild(false);
                    return new ErrorResponse("인덱스가 없어 전체 갱신을 시작했다. stats의 building이 false가 되면 다시 조회해라.");
                }

                return Dispatch(action, index, @params);
            }
            catch (Exception ex)
            {
                return new ErrorResponse($"'{action}' 실행 중 예외: {ex.Message}", new { stackTrace = ex.StackTrace });
            }
        }

        #endregion MCP Entry Point

        #region Helper

        // action 문자열 화이트리스트. Dispatch의 case와 항상 같이 관리한다
        private static readonly string[] ACTIONS = { "refs", "deps", "unused", "missing", "build", "stats" };

        private const int DEFAULT_LIMIT = 200;
        private const int MAX_DEPTH = 10;

        private static object Dispatch(string action, BBAssetRefIndex index, JObject @params)
        {
            var limit = Math.Max(1, ReadInt(@params, "limit", DEFAULT_LIMIT));
            var includeThirdParty = ReadBool(@params, "includeThirdParty", false);
            var folder = ReadString(@params, "folder")?.Replace('\\', '/');

            switch (action)
            {
                case "refs":
                case "deps":
                {
                    var target = ReadString(@params, "target");
                    var candidates = new List<string>();
                    var guid = index.ResolveGuid(target, candidates);
                    if (guid == null)
                    {
                        return candidates.Count > 1
                            ? new ErrorResponse($"'{target}' 이름의 에셋이 {candidates.Count}개다. 경로로 지정해라.", new { candidates })
                            : new ErrorResponse($"'{target}' 에셋을 찾지 못했다. GUID, Assets/ 경로, 파일 이름 중 하나로 지정해라.");
                    }

                    var depth = Math.Min(MAX_DEPTH, Math.Max(1, ReadInt(@params, "depth", 1)));
                    var hits = index.Walk(guid, action == "refs", depth, includeThirdParty);
                    var items = hits
                        .OrderBy(hit => hit.Depth)
                        .ThenBy(hit => hit.Path ?? "~", StringComparer.Ordinal)
                        .Take(limit)
                        .Select(hit => new
                        {
                            path = hit.Path ?? $"(없음) {hit.Guid}",
                            depth = hit.Depth,
                            via = hit.ViaCode ? "code" : "asset",
                            from = hit.Depth > 1 ? index.GetPath(hit.ParentGuid) : null,
                        })
                        .ToList();

                    var label = action == "refs" ? "참조하는 에셋" : "참조되는 에셋";
                    return new SuccessResponse(
                        $"{index.GetPath(guid)} — {label} {hits.Count}건 (깊이 {depth}{(hits.Count > limit ? $", {limit}건만 표시" : string.Empty)}).",
                        new { target = index.GetPath(guid), guid, total = hits.Count, items });
                }

                case "unused":
                {
                    if (folder == null)
                    {
                        return new ErrorResponse("unused에는 'folder'가 필요하다. 예: Assets/04_Prefabs");
                    }

                    var paths = index.FindUnreferenced(folder, ReadString(@params, "extension"));
                    return new SuccessResponse(
                        $"{folder} 아래 미참조 에셋 {paths.Count}건. 코드 문자열·Addressables 주소 로드는 잡지 못하니 삭제 전 확인 필요.",
                        new { total = paths.Count, items = paths.Take(limit).ToList() });
                }

                case "missing":
                {
                    var missing = index.FindMissing(folder, includeThirdParty);
                    var items = missing.Take(limit).Select(pair => new { source = pair.Key, missingGuid = pair.Value }).ToList();
                    return new SuccessResponse(
                        $"끊긴 참조 {missing.Count}건 (소스 {missing.Select(pair => pair.Key).Distinct().Count()}개 파일).",
                        new { total = missing.Count, items });
                }
            }

            return new ErrorResponse($"처리되지 않은 action '{action}'.");
        }

        private static object BuildStats(BBAssetRefIndex index)
        {
            if (index.IsBuilding)
            {
                // 갱신 중에는 사전이 스레드 풀과 메인 스레드 사이에서 교체되므로 개수를 세지 않는다
                return new { building = true };
            }

            return new
            {
                building = false,
                lastBuildError = index.LastBuildError,
                appliedChanges = index.AppliedChangeCount,
                assets = index.GuidToPath.Count,
                sources = index.Entries.Count,
                binarySources = index.Entries.Values.Count(entry => entry.Binary),
                links = index.Entries.Values.Sum(entry => entry.Refs.Count),
                codeLinks = index.CodeRefs.Values.Sum(list => list.Count),
                builtAtUtc = index.BuiltAtUtc,
                lastBuild = index.LastBuildMs > 0
                    ? new { ms = index.LastBuildMs, scanned = index.LastScannedCount, reused = index.LastReusedCount }
                    : null,
            };
        }

        /// <summary>
        /// 파라미터를 대소문자·snake_case 구분 없이 읽는다. (서버 측 케이싱 변환에 영향받지 않게 한다)
        /// </summary>
        private static JToken ReadToken(JObject @params, string name)
        {
            if (@params == null) return null;

            foreach (var prop in @params.Properties())
            {
                if (string.Equals(prop.Name.Replace("_", string.Empty), name.Replace("_", string.Empty),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return prop.Value;
                }
            }

            return null;
        }

        private static string ReadString(JObject @params, string name)
        {
            var token = ReadToken(@params, name);
            if (token == null || token.Type == JTokenType.Null) return null;

            var value = token.ToString().Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        private static bool ReadBool(JObject @params, string name, bool fallback)
        {
            var token = ReadToken(@params, name);
            if (token == null || token.Type == JTokenType.Null) return fallback;

            return token.Type == JTokenType.Boolean
                ? token.Value<bool>()
                : bool.TryParse(token.ToString(), out var parsed) ? parsed : fallback;
        }

        private static int ReadInt(JObject @params, string name, int fallback)
        {
            var token = ReadToken(@params, name);
            if (token == null || token.Type == JTokenType.Null) return fallback;

            return token.Type == JTokenType.Integer
                ? token.Value<int>()
                : int.TryParse(token.ToString(), out var parsed) ? parsed : fallback;
        }

        #endregion Helper
    }
}
#endif
