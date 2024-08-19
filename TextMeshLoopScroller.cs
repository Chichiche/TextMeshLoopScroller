using System;
using TMPro;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.TextCore;

/// <summary>
/// TextMeshProのテキストをオフセット指定によってループスクロールさせます。
/// マスキング不要、ゼロアロケーション、マルチスレッドで実行されます。
/// </summary>
/// <remarks>
/// TextMeshProのバージョンを上げるとUVがVector2からVector4になるためコンパイルエラーが発生します。
/// その場合はmeshInfo用のVector4配列と、mesh.uv用のVector2配列を二重で用意する対応が必要です。
/// </remarks>
public sealed class TextMeshLoopScroller : IDisposable
{
    /// <summary>
    /// 描画領域のRectTransformとスクロールを適用するTextMeshProのテキストを指定してインスタンスを初期化します。
    /// </summary>
    public TextMeshLoopScroller(RectTransform transform, TMP_Text text)
    {
        _transform = transform;
        _text = text;
        _text.enableWordWrapping = false;
        _text.overflowMode = TextOverflowModes.Overflow;
        Prepare();
    }

    /// <summary>
    /// 毎フレーム計算する必要のない値をキャッシュします。
    /// テキストが変化した際に呼び出してください。
    /// </summary>
    public void Prepare()
    {
        _jobHandle.Complete();
        _text.ForceMeshUpdate();
        _lastPrepareFrame = Time.frameCount;
        AllocateBuffers();
        CacheRectCorners();
        UpdateTextInfoForJob();
        UpdateCharInfosForJob();
    }

    /// <summary>
    /// スクロールの計算結果をテキストメッシュに適用します。
    /// 計算リソースの最適化のため、ジョブをスケジュールした次のフレームで呼び出すことを推奨します。
    /// </summary>
    public void Apply()
    {
        if (Time.frameCount == _lastPrepareFrame) return;
        CompleteJob();
        ApplyToMesh();
    }

    /// <summary>
    /// スクロール計算ジョブをスケジュールします。
    /// 計算結果はApply()メソッドを呼び出すことで適用されます。
    /// </summary>
    /// <param name="offset">原点からの移動量</param>
    /// <param name="spacing">ループしたテキストの先頭と末尾の間の余白</param>
    public void Schedule(Vector2 offset, Vector2 spacing)
    {
        SetExecuteParameterForJob(offset, spacing);
        ScheduleJob();
    }

    readonly RectTransform _transform;
    readonly TMP_Text _text;
    readonly Vector3[] _corners = new Vector3[4];
    int _lastPrepareFrame;
    NativeArray<TextInfoForJob> _textInfo = new( 1, Allocator.Persistent );
    NativeArray<CharInfoForJob> _charInfos;
    NativeArray<ExecuteParameterForJob> _executeParameter = new( 1, Allocator.Persistent );
    NativeArray<Vector3> _vertices;
    NativeArray<Vector2> _uvs;
    JobHandle _jobHandle;

    void AllocateBuffers()
    {
        const int VertexCount = 4;
        var bufferSize = CalcBufferSize(_text.textInfo.characterCount);
        AllocateNativeArray(ref _charInfos, bufferSize);
        AllocateNativeArray(ref _vertices, bufferSize * VertexCount);
        AllocateNativeArray(ref _uvs, bufferSize * VertexCount);
        return;

        static int CalcBufferSize(int characterCount)
        {
            const int MinCharCount = 10;
            const int BufferMultiplier = 4;
            return Math.Max(characterCount, MinCharCount) * BufferMultiplier;
        }

        static void AllocateNativeArray<T>(ref NativeArray<T> nativeArray, int bufferSize) where T : struct
        {
            if (nativeArray.Length >= bufferSize) return;
            if (nativeArray.IsCreated) nativeArray.Dispose();
            nativeArray = new NativeArray<T>(bufferSize, Allocator.Persistent);
        }
    }

    void CacheRectCorners()
    {
        _transform.GetLocalCorners(_corners);
    }

    void UpdateTextInfoForJob()
    {
        _textInfo[0] = new TextInfoForJob
        {
            AtlasSize = new Vector2(_text.font.atlasWidth, _text.font.atlasHeight),
            BottomLeft = _corners[0],
            TopRight = _corners[2],
        };
    }

    void UpdateCharInfosForJob()
    {
        var characterLookupTable = _text.font.characterLookupTable;

        for (var i = 0; i < _text.textInfo.characterCount; i++)
        {
            var charInfo = _text.textInfo.characterInfo[i];

            _charInfos[i] = charInfo.isVisible
                ? new CharInfoForJob
                {
                    IsVisible = true,
                    VertexIndex = charInfo.vertexIndex,
                    BottomLeft = charInfo.bottomLeft,
                    TopLeft = charInfo.topLeft,
                    TopRight = charInfo.topRight,
                    BottomRight = charInfo.bottomRight,
                    GlyphRect = characterLookupTable[charInfo.character].glyph.glyphRect,
                }
                : default;
        }
    }

    void SetExecuteParameterForJob(Vector3 offset, Vector3 spacing)
    {
        var rectSize = _corners[2] - _corners[0];
        _executeParameter[0] = new ExecuteParameterForJob
        {
            Offset = offset,
            LoopSize = new Vector2
            (
                Math.Max(_text.preferredWidth + spacing.x, rectSize.x + _text.fontSize),
                Math.Max(_text.preferredHeight + spacing.y, rectSize.y + _text.fontSize)
            )
        };
    }

    void ScheduleJob()
    {
        const int BatchCount = 32;

        _jobHandle = new MoveCharJob
            {
                TextInfo = _textInfo,
                CharInfos = _charInfos,
                ExecuteParameter = _executeParameter,
                CharVertices = _vertices,
                CharUVs = _uvs,
            }
            .ScheduleParallel(_text.textInfo.characterCount, BatchCount, default);
    }

    void CompleteJob()
    {
        _jobHandle.Complete();
    }

    void ApplyToMesh()
    {
        var textInfo = _text.textInfo;
        var visibleCharCount = GetVisibleCharCount(textInfo);
        if (visibleCharCount <= 0) return;
        for (var i = 0; i < textInfo.meshInfo.Length; i++)
        {
            var meshInfo = textInfo.meshInfo[i];
            if (meshInfo.vertices == null) continue;
            UpdateMeshInfo(_vertices, _uvs, meshInfo, visibleCharCount);
            UpdateMesh(_text, meshInfo, i);
        }
        return;

        static int GetVisibleCharCount(TMP_TextInfo textInfo)
        {
            var visibleCharCount = 0;
            var characterInfo = textInfo.characterInfo;
            for (var i = 0; i < textInfo.characterCount; i++)
            {
                if (! characterInfo[i].isVisible) continue;
                visibleCharCount++;
            }
            return visibleCharCount;
        }

        static void UpdateMeshInfo(in NativeArray<Vector3> vertices, in NativeArray<Vector2> uvs, TMP_MeshInfo meshInfo, int visibleCharCount)
        {
            const int VertexCount = 4;
            NativeArray<Vector3>.Copy(vertices, meshInfo.vertices, visibleCharCount * VertexCount);
            NativeArray<Vector2>.Copy(uvs, meshInfo.uvs0, visibleCharCount * VertexCount);
        }

        static void UpdateMesh(TMP_Text text, TMP_MeshInfo meshInfo, int meshIndex)
        {
            meshInfo.mesh.vertices = meshInfo.vertices;
            meshInfo.mesh.uv = meshInfo.uvs0;
            text.UpdateGeometry(meshInfo.mesh, meshIndex);
        }
    }

    [BurstCompile]
    struct MoveCharJob : IJobFor
    {
        [ReadOnly, NativeDisableParallelForRestriction] public NativeArray<TextInfoForJob> TextInfo;
        [ReadOnly] public NativeArray<CharInfoForJob> CharInfos;
        [ReadOnly, NativeDisableParallelForRestriction] public NativeArray<ExecuteParameterForJob> ExecuteParameter;
        [NativeDisableParallelForRestriction] public NativeArray<Vector3> CharVertices;
        [NativeDisableParallelForRestriction] public NativeArray<Vector2> CharUVs;

        public void Execute(int i)
        {
            var charInfo = CharInfos[i];
            if (! charInfo.IsVisible) return;
            var (isLeftOverflow, isBottomOverflow, scale) = CalcCorners(charInfo);
            CalcUvs(charInfo, isLeftOverflow, isBottomOverflow, scale);
        }

        (bool isLeftOverflow, bool isBottomOverflow, Vector2 scale) CalcCorners(in CharInfoForJob charInfo)
        {
            var rectOrigin = TextInfo[0].BottomLeft;
            var loopSize = ExecuteParameter[0].LoopSize;
            var offset = ExecuteParameter[0].Offset;

            var bottomLeft = CalcLoopedPosition(charInfo.BottomLeft);
            var topLeft = CalcLoopedPosition(charInfo.TopLeft);
            var topRight = CalcLoopedPosition(charInfo.TopRight);
            var bottomRight = CalcLoopedPosition(charInfo.BottomRight);

            var textInfo = TextInfo[0];
            var isLeftOverflow = bottomLeft.x > bottomRight.x;
            var isBottomOverflow = bottomLeft.y > topLeft.y;

            if (isLeftOverflow)
            {
                bottomLeft.x = textInfo.BottomLeft.x;
                topLeft.x = textInfo.BottomLeft.x;
            }

            if (isBottomOverflow)
            {
                bottomLeft.y = textInfo.BottomLeft.y;
                bottomRight.y = textInfo.BottomLeft.y;
            }

            bottomLeft = Clamp(bottomLeft);
            topLeft = Clamp(topLeft);
            topRight = Clamp(topRight);
            bottomRight = Clamp(bottomRight);

            CharVertices[charInfo.VertexIndex + 0] = bottomLeft;
            CharVertices[charInfo.VertexIndex + 1] = topLeft;
            CharVertices[charInfo.VertexIndex + 2] = topRight;
            CharVertices[charInfo.VertexIndex + 3] = bottomRight;

            var scale = new Vector2
            (
                (bottomRight.x - bottomLeft.x) / (charInfo.BottomRight.x - charInfo.BottomLeft.x),
                (topLeft.y - bottomLeft.y) / (charInfo.TopLeft.y - charInfo.BottomLeft.y)
            );

            return (isLeftOverflow, isBottomOverflow, scale);

            Vector3 CalcLoopedPosition(Vector3 charVertex)
            {
                var localPosition = charVertex - rectOrigin + offset;
                var loopedPosition = new Vector3
                (
                    localPosition.x % loopSize.x,
                    localPosition.y % loopSize.y,
                    localPosition.z
                );
                loopedPosition.x += loopedPosition.x < 0 ? loopSize.x : 0;
                loopedPosition.y += loopedPosition.y < 0 ? loopSize.y : 0;
                return rectOrigin + loopedPosition;
            }

            Vector3 Clamp(Vector3 position)
            {
                return new Vector3
                (
                    Math.Min(position.x, textInfo.TopRight.x),
                    Math.Min(position.y, textInfo.TopRight.y),
                    position.z
                );
            }
        }

        void CalcUvs(in CharInfoForJob charInfo, bool isLeftOverflow, bool isBottomOverflow, Vector2 scale)
        {
            var atlasSize = TextInfo[0].AtlasSize;
            var glyphRect = charInfo.GlyphRect;

            var glyphUv = new Rect
            (
                glyphRect.x / atlasSize.x,
                glyphRect.y / atlasSize.y,
                (glyphRect.x + glyphRect.width) / atlasSize.x - glyphRect.x / atlasSize.x,
                (glyphRect.y + glyphRect.height) / atlasSize.y - glyphRect.y / atlasSize.y
            );

            if (isLeftOverflow) glyphUv.x += glyphUv.width * (1f - scale.x);
            if (isBottomOverflow) glyphUv.y += glyphUv.height * (1f - scale.y);
            glyphUv.width *= scale.x;
            glyphUv.height *= scale.y;

            CharUVs[charInfo.VertexIndex + 0] = new Vector2(glyphUv.xMin, glyphUv.yMin);
            CharUVs[charInfo.VertexIndex + 1] = new Vector2(glyphUv.xMin, glyphUv.yMax);
            CharUVs[charInfo.VertexIndex + 2] = new Vector2(glyphUv.xMax, glyphUv.yMax);
            CharUVs[charInfo.VertexIndex + 3] = new Vector2(glyphUv.xMax, glyphUv.yMin);
        }
    }

    struct TextInfoForJob
    {
        public Vector2 AtlasSize;
        public Vector3 BottomLeft;
        public Vector3 TopRight;
    }

    struct CharInfoForJob
    {
        public bool IsVisible;
        public int VertexIndex;
        public Vector3 BottomLeft;
        public Vector3 TopLeft;
        public Vector3 TopRight;
        public Vector3 BottomRight;
        public GlyphRect GlyphRect;
    }

    struct ExecuteParameterForJob
    {
        public Vector3 Offset;
        public Vector2 LoopSize;
    }

    public void Dispose()
    {
        _jobHandle.Complete();
        _textInfo.Dispose();
        if (_charInfos.IsCreated) _charInfos.Dispose();
        _executeParameter.Dispose();
        if (_vertices.IsCreated) _vertices.Dispose();
        if (_uvs.IsCreated) _uvs.Dispose();
        GC.SuppressFinalize(this);
    }

    ~TextMeshLoopScroller()
    {
        Dispose();
    }
}
