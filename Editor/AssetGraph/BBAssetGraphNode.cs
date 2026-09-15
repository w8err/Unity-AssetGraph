using UnityEngine;

namespace BeastBlood.Editor.AssetGraph
{
    /// <summary>
    /// 참조 그래프 창에 그려지는 노드 하나.
    /// </summary>
    public class BBAssetGraphNode
    {
        // 창 안에서의 고유 키. 같은 에셋이 양쪽에 나올 수 있어 방향 부호를 붙인다
        public string Key { get; }

        public string Guid { get; }

        // 에셋 경로. 프로젝트에 없는 GUID(끊긴 참조)면 null
        public string Path { get; }

        // 0 = 대상, 음수 = 대상을 쓰는 쪽(역참조) 거리, 양수 = 대상이 쓰는 쪽(정참조) 거리
        public int Column { get; }

        // 대상에 한 단계 더 가까운 노드의 키. 대상 노드면 null
        public string ParentKey { get; }

        // true면 코드 문자열로 추정한 연결
        public bool ViaCode { get; }

        // 노드에 그릴 이름. 열 너비에 안 들어가면 가운데를 줄인 문자열
        public string Label { get; set; }

        // 같은 열 안에서의 순서 (배치용)
        public int Order { get; set; }

        // 스크롤 뷰 내용 좌표계 기준 위치
        public Rect Rect { get; set; }

        public BBAssetGraphNode(string key, string guid, string path, int column, string parentKey, bool viaCode)
        {
            Key = key;
            Guid = guid;
            Path = path;
            Column = column;
            ParentKey = parentKey;
            ViaCode = viaCode;
        }
    }
}
