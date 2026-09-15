namespace BeastBlood.Editor.AssetGraph
{
    /// <summary>
    /// 참조 그래프 탐색 결과 한 건.
    /// </summary>
    public readonly struct FAssetRefHit
    {
        // 찾은 에셋의 GUID
        public string Guid { get; }

        // 찾은 에셋의 경로. 프로젝트에 없는 GUID(끊긴 참조)면 null
        public string Path { get; }

        // 시작 에셋으로부터의 거리 (1 = 직접 연결)
        public int Depth { get; }

        // true면 코드 문자열로 추정한 연결
        public bool ViaCode { get; }

        // 이 에셋에 도달하기 직전 에셋의 GUID
        public string ParentGuid { get; }

        public FAssetRefHit(string guid, string path, int depth, bool viaCode, string parentGuid)
        {
            Guid = guid;
            Path = path;
            Depth = depth;
            ViaCode = viaCode;
            ParentGuid = parentGuid;
        }
    }
}
