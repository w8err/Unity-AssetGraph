using System.Linq;

using UnityEditor;

namespace BeastBlood.Editor.AssetGraph
{
    /// <summary>
    /// 에셋 임포트·삭제·이동을 받아 참조 인덱스에 파일 단위로 반영한다. (전체 재스캔 없이 인덱스를 최신으로 유지)
    /// </summary>
    /// <remarks>
    /// 인덱스를 한 번도 만든 적 없으면(캐시 파일 없음) 아무것도 하지 않는다.
    /// SVN 업데이트처럼 에디터 밖에서 바뀐 파일도 에디터가 임포트하는 시점에 반영된다.
    /// </remarks>
    public class BBAssetRefPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(
            string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            if (!BBAssetRefIndex.HasCache) return;

            BBAssetRefIndex.Instance.ApplyChanges(
                importedAssets.Concat(movedAssets),
                deletedAssets.Concat(movedFromAssetPaths));
        }
    }
}
