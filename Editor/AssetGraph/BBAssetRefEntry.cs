using System.Collections.Generic;

namespace BeastBlood.Editor.AssetGraph
{
    /// <summary>
    /// 참조 인덱스에 기록되는 소스 파일(YAML 에셋) 한 개의 스캔 결과.
    /// </summary>
    public class BBAssetRefEntry
    {
        // 스캔 당시 파일 수정 시각(UTC ticks). 크기와 함께 증분 갱신 판정에 쓴다
        public long Ticks { get; set; }

        // 스캔 당시 파일 크기(byte)
        public long Size { get; set; }

        // 텍스트(YAML)가 아니라 스캔하지 못한 파일 (LightingData, NavMesh 등)
        public bool Binary { get; set; }

        // 파일 안에 적힌 GUID 목록. 자기 자신·유니티 내장 GUID 제외, 중복 제거
        public List<string> Refs { get; set; } = new List<string>();
    }
}
