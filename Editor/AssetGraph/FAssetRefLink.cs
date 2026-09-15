namespace BeastBlood.Editor.AssetGraph
{
    /// <summary>
    /// 참조 그래프의 선 하나. 반대편 에셋 GUID와 연결 방식을 담는다.
    /// </summary>
    public readonly struct FAssetRefLink
    {
        // 반대편 에셋의 GUID
        public string Guid { get; }

        // true면 직렬화 참조가 아니라 코드 안 문자열(씬 이름 등)로 추정한 연결
        public bool ViaCode { get; }

        public FAssetRefLink(string guid, bool viaCode)
        {
            Guid = guid;
            ViaCode = viaCode;
        }
    }
}
