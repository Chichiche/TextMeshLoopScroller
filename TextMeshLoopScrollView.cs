using TMPro;
using UniRx;
using UnityEngine;

/// <summary>
/// テキストメッシュを矩形トランスフォーム内でループスクロールさせるコンポーネント
/// </summary>
public sealed class TextMeshLoopScrollView : MonoBehaviour
{
    /// <summary>
    /// 文字列の末尾とループした先頭の間隔
    /// </summary>
    public Vector2 Spacing
    {
        get => _spacing;
        set => _spacing = value;
    }

    /// <summary>
    /// 文字列のスクロール位置
    /// </summary>
    public Vector2 Offset
    {
        get => _offset;
        set => _offset = value;
    }

    /// <summary>
    /// 計算の準備を行います。文字列やトランスフォームが変化した際に呼び出してください。
    /// </summary>
    [ContextMenu(nameof( Prepare ))]
    public void Prepare()
    {
        _textMeshScroller ??= new TextMeshLoopScroller(_transform, _text).AddTo(this);
        _textMeshScroller.Prepare();
    }

    [SerializeField] private RectTransform _transform;
    [SerializeField] private TMP_Text _text;
    [SerializeField] private Vector2 _spacing;
    [SerializeField] private Vector2 _offset;

    private TextMeshLoopScroller _textMeshScroller;

    private void Update()
    {
        _textMeshScroller?.Apply();
        _textMeshScroller?.Schedule(_offset, _spacing);
    }
}
