using System;
using System.Linq;
using Unity.Jobs;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.U2D;
using Unity.Collections.LowLevel.Unsafe;
using Unity.SpriteShape.External.LibTessDotNet;

// We will enable this once Burst gets a verified final version as this attribute keeps changing.
#if ENABLE_SPRITESHAPE_BURST
using Unity.Burst;
#endif

namespace UnityEngine.U2D
{

#if ENABLE_SPRITESHAPE_BURST
    [BurstCompile]
#endif
    public struct SpriteShapeGenerator : IJob
    {

        struct JobParameters
        {
            public int4 shapeData;              // x : ClosedShape (bool) y : AdaptiveUV (bool) z : SpriteBorders (bool) w : Enable Fill Texture.
            public int4 splineData;             // x : StrtechUV. y : splineDetail z : AngleThreshold w: Collider On/Off
            public float4 curveData;            // x : ColliderPivot y : BorderPivot z : BevelCutoff w : BevelSize.
            public float4 fillData;             // x : fillScale  y : fillScale.x W z : fillScale.y H w: 0.
        }

        struct JobSpriteInfo
        {
            public float4 texRect;              // TextureRect.
            public float4 texData;              // x : GPUWidth y : GPUHeight z : TexelWidth w : TexelHeight
            public float4 uvInfo;               // x : x, y : y, z : width, w : height
            public float4 metaInfo;             // x : PPU, y : Pivot Y z : Original Rect Width w : Original Rect Height.
            public float4 border;               // Sprite Border.
        }

        struct JobAngleRange
        {
            public float4 spriteAngles;         // x, y | First Angle & z,w | Second Angle.
            public int4 spriteVariant1;         // First 4 variants here.
            public int4 spriteVariant2;         // Second 4 variants here. Total 8 max variants.
            public int4 spriteData;             // Additional Data. x : sorting Order. y : variant Count. z : render Order Max.
        };

        struct JobControlPoint
        {
            public int4 cpData;                 // x : Sprite Index y : Corner Type z : Mode w : Internal Sprite Index.
            public int4 exData;                 // x : Corner Type y: Corner Sprite z : Start/End Corner
            public float4 cpInfo;               // x : Height y : Bevel Cutoff z : Bevel Size. w : Render Order.
            public float2 position;
            public float2 tangentLt;
            public float2 tangentRt;
        };

        struct JobContourPoint
        {
            public float2 position;             // Position.
            public float2 ptData;               // x : height.
        }

        // Tessellation Structures.
        struct JobSegmentInfo
        {
            public int4 spInfo;                 // x : Begin y : End. z : Sprite w : First Sprite for that Angle Range.
            public float4 spriteInfo;           // x : width y : height z : Render Order. w: Distance of the Segment.
        };

        struct JobCornerInfo
        {
            public float2 bottom;
            public float2 top;
            public float2 left;
            public float2 right;
            public int2 cornerData;
        }

        struct JobShapeVertex
        {
            public float2 pos;
            public float2 uv;
            public float4 tan;
            public float2 meta;                 // x : height y : -
            public int2 sprite;                 // x : sprite y : is main Point.
        }

        /// <summary>
        /// Native Arrays : Scope : Initialized before and ReadOnly During Job 
        /// </summary>
        [ReadOnly]
        private JobParameters m_ShapeParams;

        [ReadOnly]
        [DeallocateOnJobCompletion]
        private NativeArray<JobSpriteInfo> m_SpriteInfos;

        [ReadOnly]
        [DeallocateOnJobCompletion]
        private NativeArray<JobSpriteInfo> m_CornerSpriteInfos;

        [ReadOnly]
        [DeallocateOnJobCompletion]
        private NativeArray<JobAngleRange> m_AngleRanges;

        /// <summary>
        /// Native Arrays : Scope : Job 
        /// </summary>
        [DeallocateOnJobCompletion]
        private NativeArray<JobSegmentInfo> m_Segments;
        private int m_SegmentCount;

        [DeallocateOnJobCompletion]
        private NativeArray<JobContourPoint> m_ContourPoints;
        private int m_ContourPointCount;

        [DeallocateOnJobCompletion]
        private NativeArray<JobCornerInfo> m_Corners;
        private int m_CornerCount;

        [DeallocateOnJobCompletion]
        private NativeArray<float2> m_TessPoints;
        private int m_TessPointCount;

        [DeallocateOnJobCompletion]
        NativeArray<JobShapeVertex> m_VertexData;

        [DeallocateOnJobCompletion]
        NativeArray<JobShapeVertex> m_OutputVertexData;

        [DeallocateOnJobCompletion]
        private NativeArray<JobControlPoint> m_ControlPoints;
        private int m_ControlPointCount;

        [DeallocateOnJobCompletion]
        private NativeArray<float2> m_CornerCoordinates;

        [DeallocateOnJobCompletion]
        private NativeArray<Vector2> m_AreaInputs;

        [DeallocateOnJobCompletion]
        private NativeArray<int> m_AreaIndices;

        [DeallocateOnJobCompletion]
        private NativeArray<Vector2> m_AreaVertices;

        [DeallocateOnJobCompletion]
        private NativeArray<float2> m_TempPoints;

        [DeallocateOnJobCompletion]
        private NativeArray<JobControlPoint> m_GeneratedControlPoints;

        [DeallocateOnJobCompletion]
        private NativeArray<int2> m_SpriteIndices;

        /// <summary>
        /// Output Native Arrays : Scope : SpriteShapeRenderer / SpriteShapeController.
        /// </summary>

        private int m_IndexArrayCount;
        public NativeArray<ushort> m_IndexArray;

        private int m_VertexArrayCount;
        public NativeSlice<Vector3> m_PosArray;
        public NativeSlice<Vector2> m_Uv0Array;
        public NativeSlice<Vector4> m_TanArray;

        private int m_GeomArrayCount;
        public NativeArray<SpriteShapeSegment> m_GeomArray;

        private int m_ColliderPointCount;
        public NativeArray<float2> m_ColliderPoints;
        public NativeArray<Bounds> m_Bounds;

        int m_IndexDataCount;
        int m_VertexDataCount;
        int m_ColliderDataCount;
        int m_ActiveIndexCount;
        int m_ActiveVertexCount;

        float2 m_FirstLT;
        float2 m_FirstLB;
        float4x4 m_Transform;

        int kModeLinear;
        int kModeContinous;
        int kModeBroken;

        int kCornerTypeOuterTopLeft;
        int kCornerTypeOuterTopRight;
        int kCornerTypeOuterBottomLeft;
        int kCornerTypeOuterBottomRight;
        int kCornerTypeInnerTopLeft;
        int kCornerTypeInnerTopRight;
        int kCornerTypeInnerBottomLeft;
        int kCornerTypeInnerBottomRight;
        int kControlPointCount;

        float kEpsilon;
        float kEpsilonRelaxed;
        float kExtendSegment;
        float kRenderQuality;
        float kOptimizeRender;
        float kColliderQuality;
        float kOptimizeCollider;
        float kLowestQualityTolerance;
        float kHighestQualityTolerance;

        #region Getters.

        // Return Vertex Data Count
        private int vertexDataCount
        {
            get { return m_VertexDataCount; }
        }

        // Return Index Data Count
        private int indexDataCount
        {
            get { return m_IndexDataCount; }
        }

        // Return Sprite Count
        private int spriteCount
        {
            get { return m_SpriteInfos.Length; }
        }

        private int cornerSpriteCount
        {
            get { return m_CornerSpriteInfos.Length; }
        }

        // Return Angle Range Count
        private int angleRangeCount
        {
            get { return m_AngleRanges.Length; }
        }

        // Return the Input Control Point Count.
        private int controlPointCount
        {
            get { return m_ControlPointCount; }
        }

        // Return the Contour Point Count.
        private int contourPointCount
        {
            get { return m_ContourPointCount; }
        }

        // Return Segment Count
        private int segmentCount
        {
            get { return m_SegmentCount; }
        }

        // Needs Collider Generaie.
        private bool hasCollider
        {
            get { return m_ShapeParams.splineData.w == 1; }
        }

        // Collider Pivot
        private float colliderPivot
        {
            get { return m_ShapeParams.curveData.x; }
        }

        // Border Pivot
        private float borderPivot
        {
            get { return m_ShapeParams.curveData.y; }
        }

        // Spline Detail
        private int splineDetail
        {
            get { return m_ShapeParams.splineData.y; }
        }

        // Is this Closed-Loop.
        private bool isCarpet
        {
            get { return m_ShapeParams.shapeData.x == 1; }
        }

        // Is Adaptive UV
        private bool isAdaptive
        {
            get { return m_ShapeParams.shapeData.y == 1; }
        }

        // Has Sprite Border.
        private bool hasSpriteBorder
        {
            get { return m_ShapeParams.shapeData.z == 1; }
        }

        #endregion

        #region Safe Getters.
        JobSpriteInfo GetSpriteInfo(int index)
        {
            if (index >= m_SpriteInfos.Length)
                throw new ArgumentException(string.Format("GetSpriteInfo accessed with invalid Index {0} / {1}", index, m_SpriteInfos.Length));
            return m_SpriteInfos[index];
        }

        JobSpriteInfo GetCornerSpriteInfo(int index)
        {
            int ai = index - 1;
            if (ai >= m_CornerSpriteInfos.Length || index == 0)
                throw new ArgumentException(string.Format("GetCornerSpriteInfo accessed with invalid Index {0} / {1}", index, m_CornerSpriteInfos.Length));
            return m_CornerSpriteInfos[ai];
        }

        JobAngleRange GetAngleRange(int index)
        {
            if (index >= m_AngleRanges.Length)
                throw new ArgumentException(string.Format("GetAngleRange accessed with invalid Index {0} / {1}", index, m_AngleRanges.Length));
            return m_AngleRanges[index];
        }

        JobControlPoint GetControlPoint(int index)
        {
            if (index >= m_ControlPoints.Length)
                throw new ArgumentException(string.Format("GetControlPoint accessed with invalid Index {0} / {1}", index, m_ControlPoints.Length));
            return m_ControlPoints[index];
        }

        JobContourPoint GetContourPoint(int index)
        {
            if (index >= m_ContourPointCount)
                throw new ArgumentException(string.Format("GetContourPoint accessed with invalid Index {0} / {1}", index, m_ContourPointCount));
            return m_ContourPoints[index];
        }

        JobSegmentInfo GetSegmentInfo(int index)
        {
            if (index >= m_SegmentCount)
                throw new ArgumentException(string.Format("GetSegmentInfo accessed with invalid Index {0} / {1}", index, m_SegmentCount));
            return m_Segments[index];
        }

        int GetContourIndex(int index)
        {
            if (index >= m_ControlPoints.Length)
                throw new ArgumentException(string.Format("GetContourIndex accessed with invalid Index {0} / {1}", index, m_ControlPoints.Length));
            return index * m_ShapeParams.splineData.y;
        }

        int GetEndContourIndexOfSegment(JobSegmentInfo isi)
        {
            int contourIndex = GetContourIndex(isi.spInfo.y) - 1;
            if (isi.spInfo.y >= m_ControlPoints.Length || isi.spInfo.y == 0)
                throw new ArgumentException("GetEndContourIndexOfSegment accessed with invalid Index");
            return contourIndex;
        }
        #endregion

        #region Utility
        static void CopyToNativeArray<T>(NativeArray<T> from, int length, ref NativeArray<T> to) where T : struct
        {
            to = new NativeArray<T>(length, Allocator.TempJob);
            for (int i = 0; i < length; ++i)
                to[i] = from[i];
        }

        static void SafeDispose<T>(NativeArray<T> na) where T : struct
        {
            if (na.Length > 0)
                na.Dispose();
        }

        static bool IsPointOnLine(float epsilon, float2 a, float2 b, float2 c)
        {
            float cp = (c.y - a.y) * (b.x - a.x) - (c.x - a.x) * (b.y - a.y);
            if (math.abs(cp) > epsilon)
                return false;

            float dp = (c.x - a.x) * (b.x - a.x) + (c.y - a.y) * (b.y - a.y);
            if (dp < 0)
                return false;

            float ba = (b.x - a.x) * (b.x - a.x) + (b.y - a.y) * (b.y - a.y);
            if (dp > ba)
                return false;
            return true;
        }

        static bool IsPointOnLines(float epsilon, float2 p1, float2 p2, float2 p3, float2 p4, float2 r)
        {
            return IsPointOnLine(epsilon, p1, p2, r) && IsPointOnLine(epsilon, p3, p4, r);
        }

        static bool LineIntersection(float epsilon, float2 p1, float2 p2, float2 p3, float2 p4, ref float2 result)
        {
            float bx = p2.x - p1.x;
            float by = p2.y - p1.y;
            float dx = p4.x - p3.x;
            float dy = p4.y - p3.y;
            float bDotDPerp = bx * dy - by * dx;
            if (math.abs(bDotDPerp) < epsilon)
            {
                return false;
            }
            float cx = p3.x - p1.x;
            float cy = p3.y - p1.y;
            float t = (cx * dy - cy * dx) / bDotDPerp;
            if ((t >= -epsilon) && (t <= 1.0f + epsilon))
            {
                result.x = p1.x + t * bx;
                result.y = p1.y + t * by;
                return true;
            }
            return false;
        }

        static float AngleBetweenVector(float2 a, float2 b)
        {
            float dot = math.dot(a, b);
            float det = (a.x * b.y) - (b.x * a.y);
            return math.atan2(det, dot) * Mathf.Rad2Deg;
        }

        static bool GenerateColumnsBi(float2 a, float2 b, float2 whsize, bool flip, ref float2 rt, ref float2 rb, float cph)
        {
            float2 v1 = flip ? (a - b) : (b - a);
            if (math.length(v1) < 1e-30f)
                return false;

            float2 rvxy = new float2(-1f, 1f);
            float2 v2 = v1.yx * rvxy;
            float2 whsizey = new float2(whsize.y * cph);
            v2 = math.normalize(v2);

            float2 v3 = v2 * whsizey;
            rt = a - v3;
            rb = a + v3;
            return true;
        }

        static bool GenerateColumnsTri(float2 a, float2 b, float2 c, float2 whsize, bool flip, ref float2 rt, ref float2 rb, float cph)
        {
            float2 rvxy = new float2(-1f, 1f);
            float2 v0 = b - a;
            float2 v1 = c - b;
            v0 = v0.yx * rvxy;
            v1 = v1.yx * rvxy;

            float2 v2 = math.normalize(v0) + math.normalize(v1);
            if (math.length(v2) < 1e-30f)
                return false;
            v2 = math.normalize(v2);
            float2 whsizey = new float2(whsize.y * cph);
            float2 v3 = v2 * whsizey;

            rt = b - v3;
            rb = b + v3;
            return true;
        }
        #endregion

        #region Input Preparation.
        void AppendCornerCoordinates(ref NativeArray<float2> corners, ref int cornerCount, float2 a, float2 b, float2 c, float2 d)
        {
            corners[cornerCount++] = a;
            corners[cornerCount++] = b;
            corners[cornerCount++] = c;
            corners[cornerCount++] = d;
        }

        unsafe void PrepareInput(SpriteShapeParameters shapeParams, int maxArrayCount, NativeArray<ShapeControlPoint> shapePoints, bool optimizeGeometry, bool updateCollider, bool optimizeCollider, float colliderPivot, float colliderDetail)
        {
            kModeLinear = 0;
            kModeContinous = 1;
            kModeBroken = 2;

            kCornerTypeOuterTopLeft = 1;
            kCornerTypeOuterTopRight = 2;
            kCornerTypeOuterBottomLeft = 3;
            kCornerTypeOuterBottomRight = 4;
            kCornerTypeInnerTopLeft = 5;
            kCornerTypeInnerTopRight = 6;
            kCornerTypeInnerBottomLeft = 7;
            kCornerTypeInnerBottomRight = 8;

            m_IndexDataCount = 0;
            m_VertexDataCount = 0;
            m_ColliderDataCount = 0;
            m_ActiveIndexCount = 0;
            m_ActiveVertexCount = 0;

            kEpsilon = 0.00001f;
            kEpsilonRelaxed = 0.001f;
            kExtendSegment = 10000.0f;

            kLowestQualityTolerance = 4.0f;
            kHighestQualityTolerance = 16.0f;

            kColliderQuality = math.clamp(colliderDetail, kLowestQualityTolerance, kHighestQualityTolerance);
            kOptimizeCollider = optimizeCollider ? 1 : 0;
            kColliderQuality = (kHighestQualityTolerance - kColliderQuality + 2.0f) * 0.002f;
            colliderPivot = (colliderPivot == 0) ? 0.001f : colliderPivot;

            kOptimizeRender = optimizeGeometry ? 1 : 0;
            kRenderQuality = math.clamp(shapeParams.splineDetail, kLowestQualityTolerance, kHighestQualityTolerance);
            kRenderQuality = (kHighestQualityTolerance - kRenderQuality + 2.0f) * 0.0002f;

            m_ShapeParams.shapeData = new int4(shapeParams.carpet ? 1 : 0, shapeParams.adaptiveUV ? 1 : 0, shapeParams.spriteBorders ? 1 : 0, shapeParams.fillTexture != null ? 1 : 0);
            m_ShapeParams.splineData = new int4(shapeParams.stretchUV ? 1 : 0, (shapeParams.splineDetail > 4) ? (int)shapeParams.splineDetail : 4, (int)shapeParams.angleThreshold, updateCollider ? 1 : 0);
            m_ShapeParams.curveData = new float4(colliderPivot, shapeParams.borderPivot, shapeParams.bevelCutoff, shapeParams.bevelSize);
            float fx = 0, fy = 0;
            if (shapeParams.fillTexture != null)
            {
                fx = (float)shapeParams.fillTexture.width * (1.0f / (float)shapeParams.fillScale);
                fy = (float)shapeParams.fillTexture.height * (1.0f / (float)shapeParams.fillScale);
            }
            m_ShapeParams.fillData = new float4(shapeParams.fillScale, fx, fy, 0);
            UnsafeUtility.MemClear(m_GeomArray.GetUnsafePtr(), m_GeomArray.Length * UnsafeUtility.SizeOf<SpriteShapeSegment>());

            m_Transform = new float4x4(shapeParams.transform.m00, shapeParams.transform.m01, shapeParams.transform.m02, shapeParams.transform.m03,
                                       shapeParams.transform.m10, shapeParams.transform.m11, shapeParams.transform.m12, shapeParams.transform.m13,
                                       shapeParams.transform.m20, shapeParams.transform.m21, shapeParams.transform.m22, shapeParams.transform.m23,
                                       shapeParams.transform.m30, shapeParams.transform.m31, shapeParams.transform.m32, shapeParams.transform.m33);

            kControlPointCount = shapePoints.Length * (int)shapeParams.splineDetail * 32;
            m_Segments = new NativeArray<JobSegmentInfo>(shapePoints.Length * 2, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            m_ContourPoints = new NativeArray<JobContourPoint>(kControlPointCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            m_TessPoints = new NativeArray<float2>(shapePoints.Length * (int)shapeParams.splineDetail * 128, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            m_VertexData = new NativeArray<JobShapeVertex>(maxArrayCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            m_OutputVertexData = new NativeArray<JobShapeVertex>(maxArrayCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            m_CornerCoordinates = new NativeArray<float2>(32, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            m_AreaInputs = new NativeArray<Vector2>(m_TessPoints.Length * 8, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            m_AreaIndices = new NativeArray<int>(m_TessPoints.Length * 64, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            m_AreaVertices = new NativeArray<Vector2>(m_TessPoints.Length * 64, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            m_TempPoints = new NativeArray<float2>(kControlPointCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            m_GeneratedControlPoints = new NativeArray<JobControlPoint>(kControlPointCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
            m_SpriteIndices = new NativeArray<int2>(kControlPointCount, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);

            int cornerCount = 0;
            AppendCornerCoordinates(ref m_CornerCoordinates, ref cornerCount, new float2(1f, 1f), new float2(0, 1f), new float2(1f, 0), new float2(0, 0));
            AppendCornerCoordinates(ref m_CornerCoordinates, ref cornerCount, new float2(1f, 0), new float2(1f, 1f), new float2(0, 0), new float2(0, 1f));
            AppendCornerCoordinates(ref m_CornerCoordinates, ref cornerCount, new float2(0, 1f), new float2(0, 0), new float2(1f, 1f), new float2(1f, 0));
            AppendCornerCoordinates(ref m_CornerCoordinates, ref cornerCount, new float2(0, 0), new float2(1f, 0), new float2(0, 1f), new float2(1f, 1f));
            AppendCornerCoordinates(ref m_CornerCoordinates, ref cornerCount, new float2(0, 0), new float2(0, 1f), new float2(1f, 0), new float2(1f, 1f));
            AppendCornerCoordinates(ref m_CornerCoordinates, ref cornerCount, new float2(0, 1f), new float2(1f, 1f), new float2(0, 0), new float2(1f, 0));
            AppendCornerCoordinates(ref m_CornerCoordinates, ref cornerCount, new float2(1f, 0), new float2(0, 0), new float2(1f, 1f), new float2(0, 1f));
            AppendCornerCoordinates(ref m_CornerCoordinates, ref cornerCount, new float2(1f, 1f), new float2(1f, 0), new float2(0, 1f), new float2(0, 0));
        }

        void TransferSprites(ref NativeArray<JobSpriteInfo> spriteInfos, Sprite[] sprites, int maxCount)
        {

            for (int i = 0; i < sprites.Length && i < maxCount; ++i)
            {
                JobSpriteInfo spriteInfo = spriteInfos[i];
                Sprite spr = sprites[i];
                if (spr != null)
                {
                    Texture2D tex = spr.texture;
                    spriteInfo.texRect = new float4(spr.textureRect.x, spr.textureRect.y, spr.textureRect.width, spr.textureRect.height);
                    spriteInfo.texData = new float4(tex.width, tex.height, tex.texelSize.x, tex.texelSize.y);
                    spriteInfo.border = new float4(spr.border.x, spr.border.y, spr.border.z, spr.border.w);
                    spriteInfo.uvInfo = new float4(spriteInfo.texRect.x / spriteInfo.texData.x, spriteInfo.texRect.y / spriteInfo.texData.y, spriteInfo.texRect.z / spriteInfo.texData.x, spriteInfo.texRect.w / spriteInfo.texData.y);
                    spriteInfo.metaInfo = new float4(spr.pixelsPerUnit, spr.pivot.y / spr.textureRect.height, spr.rect.width, spr.rect.height);

                    if (!math.any(spriteInfo.texRect))
                    {
                        Cleanup();
                        throw new ArgumentException(string.Format("{0} is packed with Tight packing or mesh type set to Tight. Please check input sprites", spr.name));
                    }
                }
                spriteInfos[i] = spriteInfo;
            }

        }

        void PrepareSprites(Sprite[] edgeSprites, Sprite[] cornerSprites)
        {
            m_SpriteInfos = new NativeArray<JobSpriteInfo>(edgeSprites.Length, Allocator.TempJob);
            TransferSprites(ref m_SpriteInfos, edgeSprites, edgeSprites.Length);

            m_CornerSpriteInfos = new NativeArray<JobSpriteInfo>(kCornerTypeInnerBottomRight, Allocator.TempJob);
            TransferSprites(ref m_CornerSpriteInfos, cornerSprites, cornerSprites.Length);
        }

        void PrepareAngleRanges(AngleRangeInfo[] angleRanges)
        {
            m_AngleRanges = new NativeArray<JobAngleRange>(angleRanges.Length, Allocator.TempJob);

            for (int i = 0; i < angleRanges.Length; ++i)
            {
                JobAngleRange angleRange = m_AngleRanges[i];
                AngleRangeInfo ari = angleRanges[i];
                int[] spr = ari.sprites;
                if (ari.start > ari.end)
                {
                    var sw = ari.start;
                    ari.start = ari.end;
                    ari.end = sw;
                }
                angleRange.spriteAngles = new float4(ari.start + 90f, ari.end + 90f, 0, 0);
                angleRange.spriteVariant1 = new int4(spr.Length > 0 ? spr[0] : -1, spr.Length > 1 ? spr[1] : -1, spr.Length > 2 ? spr[2] : -1, spr.Length > 3 ? spr[3] : -1);
                angleRange.spriteVariant2 = new int4(spr.Length > 4 ? spr[4] : -1, spr.Length > 5 ? spr[5] : -1, spr.Length > 6 ? spr[6] : -1, spr.Length > 7 ? spr[7] : -1);
                angleRange.spriteData = new int4((int)ari.order, spr.Length, 32, 0);
                m_AngleRanges[i] = angleRange;
            }
        }

        void PrepareControlPoints(NativeArray<ShapeControlPoint> shapePoints, NativeArray<SpriteShapeMetaData> metaData)
        {
            float2 zero = new float2(0, 0);
            m_ControlPoints = new NativeArray<JobControlPoint>(kControlPointCount, Allocator.TempJob);

            for (int i = 0; i < shapePoints.Length; ++i)
            {
                JobControlPoint shapePoint = m_ControlPoints[i];
                ShapeControlPoint sp = shapePoints[i];
                SpriteShapeMetaData md = metaData[i];
                shapePoint.position = new float2(sp.position.x, sp.position.y);
                shapePoint.tangentLt = (sp.mode == kModeLinear) ? zero : new float2(sp.leftTangent.x, sp.leftTangent.y);
                shapePoint.tangentRt = (sp.mode == kModeLinear) ? zero : new float2(sp.rightTangent.x, sp.rightTangent.y);
                shapePoint.cpInfo = new float4(md.height, md.bevelCutoff, md.bevelSize, 0);
                shapePoint.cpData = new int4((int)md.spriteIndex, md.corner ? 1 : 0, sp.mode, 0);
                shapePoint.exData = new int4(-1, 0, 0, 0);
                m_ControlPoints[i] = shapePoint;
            }
            m_ControlPointCount = shapePoints.Length;
            m_Corners = new NativeArray<JobCornerInfo>(shapePoints.Length, Allocator.TempJob);
            GenerateControlPoints();
        }
        #endregion 

        #region Resolve Angles for Points.
        bool WithinRange(JobAngleRange angleRange, float inputAngle)
        {
            float range = angleRange.spriteAngles.y - angleRange.spriteAngles.x;
            float angle = Mathf.Repeat(inputAngle - angleRange.spriteAngles.x, 360f);
            return (angle >= 0f && angle <= range);
        }

        bool AngleWithinRange(float t, float a, float b)
        {
            return (a != 0 && b != 0) && (t >= a && t <= b);
        }

        static float2 BezierPoint(float2 st, float2 sp, float2 ep, float2 et, float t)
        {
            float2 xt = new float2(t);
            float2 nt = new float2(1.0f - t);
            float2 x3 = new float2(3.0f);
            return (sp * nt * nt * nt) + (st * nt * nt * xt * x3) + (et * nt * xt * xt * x3) + (ep * xt * xt * xt);
        }

        static float SlopeAngle(float2 dirNormalized)
        {
            float2 dvup = new float2(0, 1f);
            float2 dvrt = new float2(1f, 0);

            float dr = math.dot(dirNormalized, dvrt);
            float du = math.dot(dirNormalized, dvup);
            float cu = math.acos(du);
            float sn = dr >= 0 ? 1.0f : -1.0f;
            float an = cu * Mathf.Rad2Deg * sn;

            // Adjust angles when direction is parallel to Up Axis.
            an = (du != 1f) ? an : 0;
            an = (du != -1f) ? an : -180f;
            return an;
        }

        static float SlopeAngle(float2 start, float2 end)
        {
            float2 dir = math.normalize(start - end);
            return SlopeAngle(dir);
        }

        bool ResolveAngle(float angle, int activeIndex, ref float renderOrder, ref int spriteIndex, ref int firstSpriteIndex)
        {
            int localRenderOrder = 0;
            int localSpriteIndex = 0;
            for (int i = 0; i < m_AngleRanges.Length; ++i)
            {
                bool withinRange = WithinRange(m_AngleRanges[i], angle);
                if (withinRange)
                {
                    int validIndex = (activeIndex < m_AngleRanges[i].spriteData.y) ? activeIndex : 0;
                    renderOrder = localRenderOrder + validIndex;
                    spriteIndex = localSpriteIndex + validIndex;
                    firstSpriteIndex = localSpriteIndex;
                    return true;
                }
                localRenderOrder += m_AngleRanges[i].spriteData.z;
                localSpriteIndex += m_AngleRanges[i].spriteData.y;
            }
            return false;
        }

        int GetSpriteIndex(int index, int previousIndex, ref int resolved)
        {
            int next = (index + 1) % controlPointCount, spriteIndex = -1, firstSpriteIndex = -1;
            float order = 0;
            var cp = GetControlPoint(index);
            float angle = SlopeAngle(GetControlPoint(next).position, cp.position);
            bool resolve = ResolveAngle(angle, cp.cpData.x, ref order, ref spriteIndex, ref firstSpriteIndex);
            resolved = resolve ? 1 : 0;
            return resolve ? spriteIndex : previousIndex;
        }
        #endregion

        #region Segments.
        void GenerateSegments()
        {
            int activeSpriteIndex = 0, activeSegmentIndex = 0, firstSpriteIndex = -1;
            JobSegmentInfo activeSegment = m_Segments[0];
            activeSegment.spInfo = int4.zero;
            activeSegment.spriteInfo = int4.zero;
            float angle = 0;

            // Generate Segments.
            for (int i = 0; i < controlPointCount; ++i)
            {
                int next = (i + 1) % controlPointCount;

                // Check for Last Point and see if we need loop-back.
                bool skipSegmenting = false;
                if (next == 0)
                {
                    if (!isCarpet)
                        continue;
                    next = 1;
                    skipSegmenting = true;
                }

                JobControlPoint iscp = GetControlPoint(i);
                JobControlPoint iscpNext = GetControlPoint(next);

                // If this segment is corner, continue.
                if (iscp.exData.x > 0 && iscp.exData.x == iscpNext.exData.x && iscp.exData.z == 1)
                    continue;

                // Resolve Angle and Order.
                int4 pointData = iscp.cpData;
                float4 pointInfo = iscp.cpInfo;

                // Get Min Max Segment.
                int mn = (i < next) ? i : next;
                int mx = (i > next) ? i : next;
                bool continueStrip = (iscp.cpData.z == kModeContinous), edgeUpdated = false;

                if (false == continueStrip || 0 == activeSegmentIndex)
                    angle = SlopeAngle(iscpNext.position, iscp.position);
                bool resolved = ResolveAngle(angle, pointData.x, ref pointInfo.w, ref pointData.w, ref firstSpriteIndex);
                if (!resolved && !skipSegmenting)
                {
                    // If we do not resolve SpriteIndex (AngleRange) just continue existing segment.
                    pointData.w = activeSpriteIndex;
                    iscp.cpData = pointData;
                    m_ControlPoints[i] = iscp;

                    // Insert Dummy Segment.
                    activeSegment = m_Segments[activeSegmentIndex];
                    activeSegment.spInfo.x = mn;
                    activeSegment.spInfo.y = mx;
                    activeSegment.spInfo.z = -1;
                    m_Segments[activeSegmentIndex] = activeSegment;
                    activeSegmentIndex++;
                    continue;
                }

                // Update current Point.
                activeSpriteIndex = pointData.w;
                iscp.cpData = pointData;
                m_ControlPoints[i] = iscp;
                if (skipSegmenting)
                    continue;

                // Check for Segments. Also check if the Segment Start has been resolved. Otherwise simply start with the next one.
                if (activeSegmentIndex != 0)
                    continueStrip = continueStrip && (m_SpriteIndices[activeSegment.spInfo.x].y != 0 && activeSpriteIndex == activeSegment.spInfo.z);

                if (continueStrip && i != (controlPointCount - 1))
                {
                    for (int s = 0; s < activeSegmentIndex; ++s)
                    {
                        activeSegment = m_Segments[s];
                        if (activeSegment.spInfo.x - mn == 1)
                        {
                            edgeUpdated = true;
                            activeSegment.spInfo.x = mn;
                            m_Segments[s] = activeSegment;
                            break;
                        }
                        if (mx - activeSegment.spInfo.y == 1)
                        {
                            edgeUpdated = true;
                            activeSegment.spInfo.y = mx;
                            m_Segments[s] = activeSegment;
                            break;
                        }
                    }
                }

                if (!edgeUpdated)
                {
                    activeSegment = m_Segments[activeSegmentIndex];
                    JobSpriteInfo sprLt = GetSpriteInfo(iscp.cpData.w);
                    activeSegment.spInfo.x = mn;
                    activeSegment.spInfo.y = mx;
                    activeSegment.spInfo.z = activeSpriteIndex;
                    activeSegment.spInfo.w = firstSpriteIndex;
                    activeSegment.spriteInfo.x = sprLt.texRect.z;
                    activeSegment.spriteInfo.y = sprLt.texRect.w;
                    activeSegment.spriteInfo.z = pointInfo.w;
                    m_Segments[activeSegmentIndex] = activeSegment;
                    activeSegmentIndex++;
                }
            }

            m_SegmentCount = activeSegmentIndex;

        }

        bool GenerateControlPoints()
        {
            // Globals.
            int activePoint = 0, activeIndex = 0;
            int startPoint = 0, endPoint = controlPointCount, lastPoint = (controlPointCount - 1);

            float2 rvxy = new float2(-1f, 1f);
            int2 sprData = new int2(0, 0);
            bool useSlice = true;
            int spriteCount = m_SpriteInfos.Length;

            // Calc and calculate Indices.
            for (int i = 0; i < controlPointCount; ++i)
            {
                var resolved = 0;
                int spriteIndex = GetSpriteIndex(i, activeIndex, ref resolved);
                sprData.x = activeIndex = spriteIndex;
                sprData.y = resolved;
                m_SpriteIndices[i] = sprData;
            }

            // Open-Ended. We simply dont allow Continous mode in End-points.
            if (!isCarpet)
            {
                JobControlPoint cp = GetControlPoint(0);
                cp.cpData.z = (cp.cpData.z == kModeContinous) ? kModeBroken : cp.cpData.z;
                m_GeneratedControlPoints[activePoint++] = cp;
                // If its not carpet, we already pre-insert start and endpoint.
                startPoint = 1;
                endPoint = controlPointCount - 1;
            }

            // Generate Intermediates.
            for (int i = startPoint; i < endPoint; ++i)
            {

                // Check if the Neighbor Points are all in Linear Mode,
                bool vc = InsertCorner(i, ref m_SpriteIndices, ref m_GeneratedControlPoints, ref activePoint);
                if (vc)
                    continue;

                // NO Corners.
                m_GeneratedControlPoints[activePoint++] = GetControlPoint(i);
            }

            // Open-Ended.
            if (!isCarpet)
            {
                JobControlPoint cp = GetControlPoint(endPoint);
                cp.cpData.z = (cp.cpData.z == kModeContinous) ? kModeBroken : cp.cpData.z;
                m_GeneratedControlPoints[activePoint++] = cp;
            }
            // If Closed Shape
            else
            {
                JobControlPoint cp = m_GeneratedControlPoints[0];
                m_GeneratedControlPoints[activePoint++] = cp;
            }

            // Copy from these intermediate Points to main Control Points.
            for (int i = 0; i < activePoint; ++i)
                m_ControlPoints[i] = m_GeneratedControlPoints[i];
            m_ControlPointCount = activePoint;

            // Calc and calculate Indices.
            for (int i = 0; i < controlPointCount; ++i)
            {
                var resolved = 0;
                int spriteIndex = GetSpriteIndex(i, activeIndex, ref resolved);
                sprData.x = activeIndex = spriteIndex;
                sprData.y = resolved;
                m_SpriteIndices[i] = sprData;
            }

            // End
            return useSlice;
        }

        float SegmentDistance(JobSegmentInfo isi)
        {
            float distance = 0;
            int stIx = GetContourIndex(isi.spInfo.x);
            int enIx = GetEndContourIndexOfSegment(isi);

            for (int i = stIx; i < enIx; ++i)
            {
                int j = i + 1;
                JobContourPoint lt = GetContourPoint(i);
                JobContourPoint rt = GetContourPoint(j);
                distance = distance + math.distance(lt.position, rt.position);
            }

            return distance;
        }

        void GenerateContour()
        {
            int controlPointContour = controlPointCount - 1;

            // Expand the Bezier.
            int ap = 0;
            float fmax = (float)(splineDetail - 1);
            for (int i = 0; i < controlPointContour; ++i)
            {
                int j = i + 1;
                JobControlPoint cp = GetControlPoint(i);
                JobControlPoint pp = GetControlPoint(j);

                float2 p0 = cp.position;
                float2 p1 = pp.position;
                float2 sp = p0;
                float2 rt = p0 + cp.tangentRt;
                float2 lt = p1 + pp.tangentLt;
                int cap = ap;
                float spd = 0, cpd = 0;

                for (int n = 0; n < splineDetail; ++n)
                {
                    JobContourPoint xp = m_ContourPoints[ap];
                    float t = (float)n / fmax;
                    float2 bp = BezierPoint(rt, p0, p1, lt, t);
                    xp.position = bp;
                    spd += math.distance(bp, sp);
                    m_ContourPoints[ap++] = xp;
                    sp = bp;
                }

                sp = p0;
                for (int n = 0; n < splineDetail; ++n)
                {
                    JobContourPoint xp = m_ContourPoints[cap];
                    cpd += math.distance(xp.position, sp);
                    xp.ptData.x = math.lerp(cp.cpInfo.x, pp.cpInfo.x, cpd / spd);
                    m_ContourPoints[cap++] = xp;
                    sp = xp.position;
                }

            }

            // End
            m_ContourPointCount = ap;
        }

        void TessellateContour()
        {

            int tessPoints = 0;

            // Create Tessallator if required.
            for (int i = 0; i < contourPointCount; ++i)
            {
                if ((i + 1) % splineDetail == 0)
                    continue;
                int h = (i == 0) ? (contourPointCount - 1) : (i - 1);
                int j = (i + 1) % contourPointCount;
                h = (i % splineDetail == 0) ? (h - 1) : h;

                JobContourPoint pp = GetContourPoint(h);
                JobContourPoint cp = GetContourPoint(i);
                JobContourPoint np = GetContourPoint(j);

                float2 cpd = cp.position - pp.position;
                float2 npd = np.position - cp.position;
                if (math.length(cpd) < kEpsilon || math.length(npd) < kEpsilon)
                    continue;

                float2 vl = math.normalize(cpd);
                float2 vr = math.normalize(npd);

                vl = new float2(-vl.y, vl.x);
                vr = new float2(-vr.y, vr.x);

                float2 va = math.normalize(vl) + math.normalize(vr);
                float2 vn = math.normalize(va);

                if (math.any(va) && math.any(vn))
                    m_TessPoints[tessPoints++] = cp.position + (vn * borderPivot);
            }

            m_TessPointCount = tessPoints;

            // Fill Geom. Generate in Native code until we have a reasonably fast enough Tessellation in NativeArray based Jobs.
            SpriteShapeSegment geom = m_GeomArray[0];
            Vector3 pos = m_PosArray[0];

            geom.vertexCount = 0;
            geom.geomIndex = 0;
            geom.indexCount = 0;
            geom.spriteIndex = -1;

            // Fill Geometry. Check if Fill Texture and Fill Scale is Valid.
            if (math.all(m_ShapeParams.shapeData.xw))
            {
                // Fill Geometry. Check if Fill Texture and Fill Scale is Valid.
                if (m_TessPointCount > 0)
                {
                    if (kOptimizeRender > 0)
                        OptimizePoints(kRenderQuality, ref m_TessPoints, ref m_TessPointCount);

                    var inputs = new ContourVertex[m_TessPointCount];
                    for (int i = 0; i < m_TessPointCount; ++i)
                        inputs[i] = new ContourVertex() { Position = new Vec3() { X = m_TessPoints[i].x, Y = m_TessPoints[i].y } };

                    Tess tess = new Tess();
                    tess.AddContour(inputs, ContourOrientation.Original);
                    tess.Tessellate(WindingRule.NonZero, ElementType.Polygons, 3);

                    var indices = tess.Elements.Select(i => (UInt16)i).ToArray();
                    var vertices = tess.Vertices.Select(v => new Vector2(v.Position.X, v.Position.Y)).ToArray();
                    m_IndexDataCount = indices.Length;
                    m_VertexDataCount = vertices.Length;

                    if (vertices.Length > 0)
                    {
                        for (m_ActiveIndexCount = 0; m_ActiveIndexCount < m_IndexDataCount; ++m_ActiveIndexCount)
                        {
                            m_IndexArray[m_ActiveIndexCount] = indices[m_ActiveIndexCount];
                        }
                        for (m_ActiveVertexCount = 0; m_ActiveVertexCount < m_VertexDataCount; ++m_ActiveVertexCount)
                        {
                            pos = new Vector3(vertices[m_ActiveVertexCount].x, vertices[m_ActiveVertexCount].y, 0);
                            m_PosArray[m_ActiveVertexCount] = pos;
                        }

                        geom.indexCount = m_ActiveIndexCount;
                        geom.vertexCount = m_ActiveVertexCount;
                    }
                }
            }

            if (m_TanArray.Length > 1)
            {
                for (int i = 0; i < m_ActiveVertexCount; ++i)
                    m_TanArray[i] = new Vector4(1.0f, 0, 0, -1.0f);
            }

            m_GeomArray[0] = geom;

        }

        void CalculateBoundingBox()
        {
            Bounds bounds = new Bounds();

            if (vertexDataCount > 0)
            { 
                for (int i = 0; i < vertexDataCount; ++i)
                {
                    Vector3 pos = m_PosArray[i];
                    bounds.Encapsulate(pos);
                }
            }            
            {
                for (int i = 0; i < contourPointCount; ++i)
                {
                    Vector3 pos = new Vector3(m_ContourPoints[i].position.x, m_ContourPoints[i].position.y, 0);
                    bounds.Encapsulate(pos);
                }
            }

            m_Bounds[0] = bounds;
        }

        void CalculateTexCoords()
        {

            SpriteShapeSegment geom = m_GeomArray[0];
            if (m_ShapeParams.splineData.x > 0)
            {
                float3 ext = m_Bounds[0].extents * 2;
                float3 min = m_Bounds[0].center - m_Bounds[0].extents;
                for (int i = 0; i < geom.vertexCount; ++i)
                {
                    Vector3 pos = m_PosArray[i];
                    Vector2 uv0 = m_Uv0Array[i];
                    float3 uv = ((new float3(pos.x, pos.y, pos.z) - min) / ext) * m_ShapeParams.fillData.x;
                    uv0.x = uv.x;
                    uv0.y = uv.y;
                    m_Uv0Array[i] = uv0;
                }
            }
            else
            {
                for (int i = 0; i < geom.vertexCount; ++i)
                {
                    Vector3 pos = m_PosArray[i];
                    Vector2 uv0 = m_Uv0Array[i];
                    float3 uv = math.transform(m_Transform, new float3(pos.x, pos.y, pos.z));
                    uv0.x = uv.x / m_ShapeParams.fillData.y;
                    uv0.y = uv.y / m_ShapeParams.fillData.z;
                    m_Uv0Array[i] = uv0;
                }
            }

        }

        void CopyVertexData(ref NativeSlice<Vector3> outPos, ref NativeSlice<Vector2> outUV0, ref NativeSlice<Vector4> outTan, int outIndex, NativeArray<JobShapeVertex> inVertices, int inIndex, float pivot, float sOrder)
        {
            Vector3 iscp = outPos[outIndex];
            Vector2 iscu = outUV0[outIndex];

            float3 v0 = new float3(inVertices[inIndex].pos.x, inVertices[inIndex].pos.y, sOrder);
            float3 v1 = new float3(inVertices[inIndex + 1].pos.x, inVertices[inIndex + 1].pos.y, sOrder);
            float3 v2 = new float3(inVertices[inIndex + 2].pos.x, inVertices[inIndex + 2].pos.y, sOrder);
            float3 v3 = new float3(inVertices[inIndex + 3].pos.x, inVertices[inIndex + 3].pos.y, sOrder);

            float3 lt = (v2 - v0) * pivot;
            float3 rt = (v3 - v1) * pivot;
            v0 = v0 + lt;
            v2 = v2 + lt;
            v1 = v1 + rt;
            v3 = v3 + rt;

            outPos[outIndex] = v0;            
            outUV0[outIndex] = inVertices[inIndex].uv;
            outPos[outIndex + 1] = v1;
            outUV0[outIndex + 1] = inVertices[inIndex + 1].uv;
            outPos[outIndex + 2] = v2;
            outUV0[outIndex + 2] = inVertices[inIndex + 2].uv;
            outPos[outIndex + 3] = v3;
            outUV0[outIndex + 3] = inVertices[inIndex + 3].uv;

            if (outTan.Length > 1)
            {
                outTan[outIndex] = inVertices[inIndex].tan;
                outTan[outIndex + 1] = inVertices[inIndex + 1].tan;
                outTan[outIndex + 2] = inVertices[inIndex + 2].tan;
                outTan[outIndex + 3] = inVertices[inIndex + 3].tan;
            }
        }

        int CopySegmentRenderData(JobSpriteInfo ispr, ref NativeSlice<Vector3> outPos, ref NativeSlice<Vector2> outUV0, ref NativeSlice<Vector4> outTan, ref int outCount, ref NativeArray<ushort> indexData, ref int indexCount, NativeArray<JobShapeVertex> inVertices, int inCount, float sOrder)
        {
            if (inCount < 4)
                return -1;

            int localVertex = 0;
            float pivot = 0.5f - ispr.metaInfo.y;
            int finalCount = indexCount + inCount;
            if (finalCount >= indexData.Length)
                throw new InvalidOperationException("Mesh data has reached Limits. Please try dividing shape into smaller blocks.");

            for (int i = 0; i < inCount; i = i + 4, outCount = outCount + 4, localVertex = localVertex + 4)
            {
                CopyVertexData(ref outPos, ref outUV0, ref outTan, outCount, inVertices, i, pivot, sOrder);
                indexData[indexCount++] = (ushort)(localVertex);
                indexData[indexCount++] = (ushort)(3 + localVertex);
                indexData[indexCount++] = (ushort)(1 + localVertex);
                indexData[indexCount++] = (ushort)(localVertex);
                indexData[indexCount++] = (ushort)(2 + localVertex);
                indexData[indexCount++] = (ushort)(3 + localVertex);
            }
            return outCount;
        }

        void TessellateSegment(JobSpriteInfo sprInfo, JobSegmentInfo segment, float2 whsize, float4 border, float pxlWidth, bool useClosure, bool validHead, bool validTail, NativeArray<JobShapeVertex> vertices, int vertexCount, ref NativeArray<JobShapeVertex> outputVertices, ref int outputCount)
        {
            int outputVertexCount = 0;
            float2 zero = new float2(0, 0);
            float2 lt = zero, lb = zero, rt = zero, rb = zero;
            var column0 = new JobShapeVertex();
            var column1 = new JobShapeVertex();
            var column2 = new JobShapeVertex();
            var column3 = new JobShapeVertex();


            int cms = vertexCount - 1;
            int lcm = cms - 1;
            var sprite = vertices[0].sprite;

            float uvDist = 0;
            float uvStart = border.x;
            float uvEnd = whsize.x - border.z;
            float uvTotal = whsize.x;
            float uvInter = uvEnd - uvStart;
            float uvNow = uvStart / uvTotal;
            float dt = uvInter / pxlWidth;

            // Generate Render Inputs.
            for (int i = 0; i < cms; ++i)
            {
                bool lc = (cms > 1) && (i == lcm);
                bool im = (i != 0 && !lc);
                bool sa = false, sb = false;

                JobShapeVertex cs = vertices[i];
                JobShapeVertex ns = vertices[i + 1];

                float2 es = lc ? cs.pos : vertices[i + 2].pos;
                lt = column1.pos;
                lb = column3.pos;
                sa = true;

                if (im)
                {
                    // Left from Previous.
                    sb = GenerateColumnsTri(cs.pos, ns.pos, es, whsize, lc, ref rt, ref rb, ns.meta.x * 0.5f);
                }
                else
                {
                    if (!lc)
                    {
                        JobControlPoint icp = GetControlPoint(segment.spInfo.x);
                        var nsPos = ns.pos;
                        if (math.any(icp.tangentRt))
                            nsPos = icp.tangentRt + cs.pos;
                        sa = GenerateColumnsBi(cs.pos, nsPos, whsize, false, ref lt, ref lb, cs.meta.x * 0.5f);
                    }
                    if (lc && useClosure)
                    {
                        rb = m_FirstLB;
                        rt = m_FirstLT;
                    }
                    else
                    {
                        var esPos = es;
                        if (i == lcm)
                        { 
                            JobControlPoint jcp = GetControlPoint(segment.spInfo.y);
                            if (math.any(jcp.tangentLt))
                                esPos = jcp.tangentLt + ns.pos;
                        }
                        sb = GenerateColumnsBi(ns.pos, esPos, whsize, lc, ref rt, ref rb, ns.meta.x * 0.5f);
                    }
                }

                if (i == 0 && segment.spInfo.x == 0)
                {
                    m_FirstLB = lb;
                    m_FirstLT = lt;
                }

                if (!((math.any(lt) || math.any(lb)) && (math.any(rt) || math.any(rb))))                
                    continue;
                
                // default tan (1, 0, 0, -1) which is along uv. same here.
                float2 nlt = math.normalize(rt - lt);
                float4 tan = new float4(nlt.x, nlt.y, 0, -1.0f);
                column0.pos = lt;
                column0.meta = cs.meta;
                column0.sprite = sprite;
                column0.tan = tan;
                column1.pos = rt;
                column1.meta = ns.meta;
                column1.sprite = sprite;
                column1.tan = tan;
                column2.pos = lb;
                column2.meta = cs.meta;
                column2.sprite = sprite;
                column2.tan = tan;
                column3.pos = rb;
                column3.meta = ns.meta;
                column3.sprite = sprite;
                column3.tan = tan;

                // Calculate UV.
                if (validHead && i == 0)
                {
                    column0.uv.x = column0.uv.y = column1.uv.y = column2.uv.x = 0;
                    column1.uv.x = column3.uv.x = border.x / whsize.x;
                    column2.uv.y = column3.uv.y = 1.0f;
                }
                else if (validTail && i == lcm)
                {
                    column0.uv.y = column1.uv.y = 0;
                    column0.uv.x = column2.uv.x = (whsize.x - border.z) / whsize.x;
                    column1.uv.x = column2.uv.y = column3.uv.x = column3.uv.y = 1.0f;
                }
                else
                {
                    if ((uvInter - uvDist) < kEpsilonRelaxed)
                    {
                        uvNow = uvStart / uvTotal;
                        uvDist = 0;
                    }

                    uvDist = uvDist + (math.distance(ns.pos, cs.pos) * dt);
                    float uvNext = (uvDist + uvStart) / uvTotal;

                    if ((uvDist - uvInter) > kEpsilonRelaxed)
                    {
                        uvNext = uvEnd / uvTotal;
                        uvDist = uvEnd;
                    }

                    column0.uv.y = column1.uv.y = 0;
                    column0.uv.x = column2.uv.x = uvNow;
                    column1.uv.x = column3.uv.x = uvNext;
                    column2.uv.y = column3.uv.y = 1.0f;
                    uvNow = uvNext;
                }
                
                {
                    // Fix UV and Copy.
                    column0.uv.x = (column0.uv.x * sprInfo.uvInfo.z) + sprInfo.uvInfo.x;
                    column0.uv.y = (column0.uv.y * sprInfo.uvInfo.w) + sprInfo.uvInfo.y;
                    outputVertices[outputVertexCount++] = column0;

                    column1.uv.x = (column1.uv.x * sprInfo.uvInfo.z) + sprInfo.uvInfo.x;
                    column1.uv.y = (column1.uv.y * sprInfo.uvInfo.w) + sprInfo.uvInfo.y;
                    outputVertices[outputVertexCount++] = column1;

                    column2.uv.x = (column2.uv.x * sprInfo.uvInfo.z) + sprInfo.uvInfo.x;
                    column2.uv.y = (column2.uv.y * sprInfo.uvInfo.w) + sprInfo.uvInfo.y;
                    outputVertices[outputVertexCount++] = column2;

                    column3.uv.x = (column3.uv.x * sprInfo.uvInfo.z) + sprInfo.uvInfo.x;
                    column3.uv.y = (column3.uv.y * sprInfo.uvInfo.w) + sprInfo.uvInfo.y;
                    outputVertices[outputVertexCount++] = column3;
                }
            }
            outputCount = outputVertexCount;
        }

        bool SkipSegment(JobSegmentInfo isi)
        {
            // Start the Generation.            
            bool skip = (isi.spInfo.z < 0);
            if (!skip)
            {
                JobSpriteInfo ispr = GetSpriteInfo(isi.spInfo.z);
                skip = (math.any(ispr.uvInfo) == false);
            }
            if (skip)
            {
                int cis = GetContourIndex(isi.spInfo.x);
                int cie = GetEndContourIndexOfSegment(isi);
                while (cis < cie)
                {
                    JobContourPoint icp = GetContourPoint(cis);
                    m_ColliderPoints[m_ColliderDataCount++] = icp.position;
                    cis++;
                }
            }
            return skip;
        }

        void TessellateSegments()
        {

            JobControlPoint iscp = GetControlPoint(0);
            bool disableHead = (iscp.cpData.z == kModeContinous && isCarpet);

            // Determine Distance of Segment.
            for (int i = 0; i < segmentCount; ++i)
            {
                // Calculate Segment Distances.
                JobSegmentInfo isi = GetSegmentInfo(i);
                if (isi.spriteInfo.z >= 0)
                {
                    isi.spriteInfo.w = SegmentDistance(isi);
                    m_Segments[i] = isi;
                }
            }

            float2 zero = new float2(0, 0);
            float2 firstLT = zero;
            float2 firstLB = zero;
            float2 ec = zero;

            for (int i = 0; i < segmentCount; ++i)
            {
                // Tessellate the Segment.
                JobSegmentInfo isi = GetSegmentInfo(i);
                bool skip = SkipSegment(isi);
                if (skip)
                    continue;

                // Internal Data : x, y : pos z : height w : renderIndex
                JobShapeVertex isv = m_VertexData[0];
                JobSpriteInfo ispr = GetSpriteInfo(isi.spInfo.z);

                int vertexCount = 0;
                int sprIx = isi.spInfo.z;
                float rpunits = 1.0f / ispr.metaInfo.x;
                float2 whsize = new float2(ispr.metaInfo.z, ispr.metaInfo.w) * rpunits;
                float4 border = ispr.border * rpunits;

                bool validHead = hasSpriteBorder && (border.x > 0);
                bool validTail = hasSpriteBorder && (border.z > 0);

                // Generate the UV Increments.
                float extendUV = 0;
                float stPixelU = border.x;
                float enPixelU = whsize.x - border.z;
                float pxlWidth = enPixelU - stPixelU;
                float segmentD = isi.spriteInfo.w;
                float uIncStep = math.floor(segmentD / pxlWidth);
                uIncStep = uIncStep == 0 ? 1f : uIncStep;
                pxlWidth = isAdaptive ? (segmentD / uIncStep) : pxlWidth;

                // Check for any invalid Sizes.
                if (pxlWidth < kEpsilon)
                {
                    Cleanup();
                    throw new ArgumentException("One of the sprites seem to have Invalid Borders. Please check Input Sprites.");
                }

                // Start the Generation.
                int stIx = GetContourIndex(isi.spInfo.x);
                int enIx = GetEndContourIndexOfSegment(isi);

                // Single Segment Loop.
                if (stIx == 0)
                    validHead = (validHead && !disableHead);

                // Do we have a Sprite Head Slice
                if (validHead)
                {
                    JobContourPoint icp = GetContourPoint(stIx);
                    float2 v1 = icp.position;
                    float2 v2 = GetContourPoint(stIx + 1).position;
                    isv.pos = v1 + (math.normalize(v1 - v2) * border.x);
                    isv.meta.x = icp.ptData.x;
                    isv.sprite.x = sprIx;
                    m_VertexData[vertexCount++] = isv;
                }

                // Generate the Strip.
                float sl = 0;
                int it = stIx, nt = 0;
                while (it < enIx)
                {
                    nt = it + 1;
                    JobContourPoint icp = GetContourPoint(it);
                    JobContourPoint ncp = GetContourPoint(nt);

                    float2 sp = icp.position;
                    float2 ip = sp;
                    float2 ep = ncp.position;
                    float2 df = ep - sp;
                    float al = math.length(df);
                    if (al > kEpsilon)
                    {
                        float sh = icp.ptData.x, eh = ncp.ptData.x, hl = 0;
                        sl = sl + al;

                        var addtail = true;
                        float2 step = math.normalize(df);
                        isv.pos = icp.position;
                        isv.meta.x = icp.ptData.x;
                        isv.sprite.x = sprIx;
                        if (vertexCount > 0)
                        { 
                            var dt = math.length(m_VertexData[vertexCount-1].pos - isv.pos);
                            addtail = dt > kEpsilonRelaxed;
                        }
                        if (addtail)
                            m_VertexData[vertexCount++] = isv;

                        while (sl > pxlWidth)
                        {
                            float _uv = pxlWidth - extendUV;
                            float2 uv = new float2(_uv);
                            ip = sp + (step * uv);
                            hl = hl + math.length(ip - sp);

                            isv.pos = ip;
                            isv.meta.x = math.lerp(sh, eh, hl / al);
                            isv.sprite.x = sprIx;
                            if (math.any(m_VertexData[vertexCount-1].pos - isv.pos))
                                m_VertexData[vertexCount++] = isv;

                            sl = sl - pxlWidth;
                            sp = ip;
                            extendUV = 0;
                        }
                        extendUV = sl;
                    }
                    it++;
                }

                // The Remains from the above Loop. Finish the Curve.
                if (sl > kEpsilon)
                {
                    JobContourPoint ecp = GetContourPoint(enIx);
                    isv.pos = ecp.position;
                    isv.meta.x = ecp.ptData.x;
                    isv.sprite.x = sprIx;
                    m_VertexData[vertexCount++] = isv;
                }

                // Generate Tail
                if (validTail)
                {
                    JobContourPoint icp = GetContourPoint(enIx);
                    float2 v1 = icp.position;
                    float2 v2 = GetContourPoint(enIx - 1).position;
                    isv.pos = v1 + (math.normalize(v1 - v2) * border.z);
                    isv.meta.x = icp.ptData.x;
                    isv.sprite.x = sprIx;
                    m_VertexData[vertexCount++] = isv;
                }

                // Generate the Renderer Data.
                int outputCount = 0;
                bool useClosure = (m_ControlPoints[0].cpData.z == kModeContinous) && (isi.spInfo.y == controlPointCount - 1);
                TessellateSegment(ispr, isi, whsize, border, pxlWidth, useClosure, validHead, validTail, m_VertexData, vertexCount, ref m_OutputVertexData, ref outputCount);
                if (outputCount == 0)
                    continue;
                var z = -0.01f + ((float)isi.spInfo.z * kEpsilonRelaxed) + ((float)-i * kEpsilon);
                CopySegmentRenderData(ispr, ref m_PosArray, ref m_Uv0Array, ref m_TanArray, ref m_VertexDataCount, ref m_IndexArray, ref m_IndexDataCount, m_OutputVertexData, outputCount, z);

                if (hasCollider)
                {
                    JobSpriteInfo isprc = GetSpriteInfo(isi.spInfo.w);
                    if (isprc.metaInfo.x == 0)
                        isprc = ispr;
                    outputCount = 0;
                    rpunits = 1.0f / isprc.metaInfo.x;
                    whsize = new float2(isprc.metaInfo.z, isprc.metaInfo.w) * rpunits;
                    border = isprc.border * rpunits;
                    stPixelU = border.x;
                    enPixelU = whsize.x - border.z;
                    pxlWidth = enPixelU - stPixelU;
                    TessellateSegment(isprc, isi, whsize, border, pxlWidth, useClosure, validHead, validTail, m_VertexData, vertexCount, ref m_OutputVertexData, ref outputCount);
                    ec = UpdateCollider(isi, isprc, m_OutputVertexData, outputCount, ref m_ColliderPoints, ref m_ColliderDataCount);
                }

                // Geom Data
                var geom = m_GeomArray[i + 1];
                geom.geomIndex = i + 1;
                geom.indexCount = m_IndexDataCount - m_ActiveIndexCount;
                geom.vertexCount = m_VertexDataCount - m_ActiveVertexCount;
                geom.spriteIndex = isi.spInfo.z;
                m_GeomArray[i + 1] = geom;

                // Exit
                m_ActiveIndexCount = m_IndexDataCount;
                m_ActiveVertexCount = m_VertexDataCount;
            }

            // Copy Collider, Copy Render Data.
            m_GeomArrayCount = segmentCount + 1;
            m_IndexArrayCount = m_IndexDataCount;
            m_VertexArrayCount = m_VertexDataCount;
            m_ColliderPointCount = m_ColliderDataCount;
        }

        #endregion

        #region Corners

        bool AttachCorner(int cp, int ct, JobSpriteInfo ispr, ref NativeArray<JobControlPoint> newPoints, ref int activePoint)
        {
            // Correct Left.
            float2 zero = new float2(0, 0);
            int pp = (cp == 0) ? (controlPointCount - 1) : (cp - 1);
            int np = (cp + 1) % controlPointCount;

            JobControlPoint lcp = GetControlPoint(pp);
            JobControlPoint ccp = GetControlPoint(cp);
            JobControlPoint rcp = GetControlPoint(np);

            float rpunits = 1.0f / ispr.metaInfo.x;
            float2 whsize = new float2(ispr.texRect.z, ispr.texRect.w) * rpunits;
            float4 border = ispr.border * rpunits;

            // Generate the UV Increments.
            float stPixelV = border.y;
            float enPixelV = whsize.y - border.y;
            float pxlWidth = enPixelV - stPixelV;   // pxlWidth is the square size of the corner sprite.

            // Generate the LeftTop, LeftBottom, RightTop & RightBottom for both sides.
            float2 lt0 = zero;
            float2 lb0 = zero;
            float2 rt0 = zero;
            float2 rb0 = zero;
            GenerateColumnsBi(lcp.position, ccp.position, whsize, false, ref lb0, ref lt0, 0.5f);
            GenerateColumnsBi(ccp.position, lcp.position, whsize, false, ref rt0, ref rb0, 0.5f);

            float2 lt1 = zero;
            float2 lb1 = zero;
            float2 rt1 = zero;
            float2 rb1 = zero;
            GenerateColumnsBi(ccp.position, rcp.position, whsize, false, ref lb1, ref lt1, 0.5f);
            GenerateColumnsBi(rcp.position, ccp.position, whsize, false, ref rt1, ref rb1, 0.5f);

            rt0 = rt0 + (math.normalize(rt0 - lt0) * kExtendSegment);
            rb0 = rb0 + (math.normalize(rb0 - lb0) * kExtendSegment);
            lt1 = lt1 + (math.normalize(lt1 - rt1) * kExtendSegment);
            lb1 = lb1 + (math.normalize(lb1 - rb1) * kExtendSegment);

            float2 tp = zero;
            float2 bt = zero;

            // Generate Intersection of the Bottom Line Segments.
            bool t = LineIntersection(kEpsilon, lt0, rt0, lt1, rt1, ref tp);
            bool b = LineIntersection(kEpsilon, lb0, rb0, lb1, rb1, ref bt);
            if (!b && !t)
                return false;

            float2 pt = ccp.position;
            float2 lt = lcp.position - pt;
            float2 rt = rcp.position - pt;

            float ld = math.length(lt);
            float rd = math.length(rt);

            if (ld < pxlWidth || rd < pxlWidth)
                return false;

            float lrd = 0, rrd = 0;
            float a = AngleBetweenVector(math.normalize(lcp.position - ccp.position), math.normalize(rcp.position - ccp.position));
            if (a > 0)
            {
                lrd = ld - math.distance(lb0, bt);
                rrd = rd - math.distance(bt, rb1);
            }
            else
            {
                lrd = ld - math.distance(lt0, tp);
                rrd = rd - math.distance(tp, rt1);
            }

            float2 la = pt + (math.normalize(lt) * lrd);
            float2 ra = pt + (math.normalize(rt) * rrd);

            ccp.exData.x = ct;
            ccp.exData.z = 1;
            ccp.position = la;
            newPoints[activePoint++] = ccp;

            ccp.exData.x = ct;
            ccp.exData.z = 0;
            ccp.position = ra;
            newPoints[activePoint++] = ccp;

            JobCornerInfo iscp = m_Corners[m_CornerCount];
            if (a > 0)
            {
                iscp.bottom = bt;
                iscp.top = tp;
                GenerateColumnsBi(la, lcp.position, whsize, false, ref lt0, ref lb0, ispr.metaInfo.y);
                GenerateColumnsBi(ra, rcp.position, whsize, false, ref lt1, ref lb1, ispr.metaInfo.y);
                iscp.left = lt0;
                iscp.right = lb1;
            }
            else
            {
                iscp.bottom = tp;
                iscp.top = bt;
                GenerateColumnsBi(la, lcp.position, whsize, false, ref lt0, ref lb0, ispr.metaInfo.y);
                GenerateColumnsBi(ra, rcp.position, whsize, false, ref lt1, ref lb1, ispr.metaInfo.y);
                iscp.left = lb0;
                iscp.right = lt1;
            }
            iscp.cornerData.x = ct;
            iscp.cornerData.y = activePoint;
            m_Corners[m_CornerCount] = iscp;

            m_CornerCount++;
            return true;
        }

        float2 CornerTextureCoordinate(int cornerType, int index)
        {
            int cornerArrayIndex = (cornerType - 1) * 4;
            return m_CornerCoordinates[cornerArrayIndex + index];
        }

        int CalculateCorner(int index, float angle, float2 lt, float2 rt)
        {
            var ct = 0;
            float slope = SlopeAngle(lt);
            var slopePairs = new float2[] 
            {
                new float2(-135.0f, -35.0f),
                new float2(35.0f, 135.0f),
                new float2(-35.0f, 35.0f),
                new float2(-135.0f, 135.0f)
            };
            var cornerPairs = new int2[] 
            {
                new int2(kCornerTypeInnerTopLeft, kCornerTypeOuterBottomLeft),
                new int2(kCornerTypeInnerBottomRight, kCornerTypeOuterTopRight),
                new int2(kCornerTypeInnerTopRight, kCornerTypeOuterTopLeft),
                new int2(kCornerTypeInnerBottomLeft, kCornerTypeOuterBottomRight)
            };
            for (int i = 0; i < 3; ++i)
            {
                if ( slope > slopePairs[i].x && slope < slopePairs[i].y )
                {
                    ct = (angle > 0) ? cornerPairs[i].x : cornerPairs[i].y;
                    break;
                }
            }
            if (ct == 0)
            {
                ct = (angle > 0) ? kCornerTypeInnerBottomLeft : kCornerTypeOuterBottomRight;
            }
            return ct;

        }

        bool InsertCorner(int index, ref NativeArray<int2> cpSpriteIndices, ref NativeArray<JobControlPoint> newPoints, ref int activePoint)
        {
            int i = (index == 0) ? (controlPointCount - 1) : (index - 1);
            int k = (index + 1) % controlPointCount;

            // Check if we have valid Sprites.
            if (cpSpriteIndices[i].x >= spriteCount || cpSpriteIndices[index].x >= spriteCount)
                return false;

            // Check if they have been resolved.
            if (cpSpriteIndices[i].y == 0 || cpSpriteIndices[index].y == 0)
                return false;

            JobControlPoint pcp = GetControlPoint(i);
            JobControlPoint icp = GetControlPoint(index);
            JobControlPoint ncp = GetControlPoint(k);

            // Check if the Mode of control Point and previous neighbor is same. Also check if Corner Toggle is enabled.
            if (icp.cpData.y == 0 || pcp.cpData.z != kModeLinear || icp.cpData.z != kModeLinear || ncp.cpData.z != kModeLinear)
                return false;

            // Check if the Height of the Control Points match
            if (pcp.cpInfo.x != icp.cpInfo.x || icp.cpInfo.x != ncp.cpInfo.x)
                return false;

            JobSpriteInfo psi = GetSpriteInfo(cpSpriteIndices[i].x);
            JobSpriteInfo isi = GetSpriteInfo(cpSpriteIndices[index].x);

            // Check if the Sprites Pivot matches. Otherwise not allowed. // psi.uvInfo.w != isi.uvInfo.w (no more height checks)
            if (psi.metaInfo.y != 0.5f || psi.metaInfo.y != isi.metaInfo.y)
                return false;

            // Now perform expensive stuff like angles etc..
            float2 idir = math.normalize(ncp.position - icp.position);
            float2 ndir = math.normalize(pcp.position - icp.position);
            float angle = AngleBetweenVector(idir, ndir);
            float angleAbs = math.abs(angle);
            bool corner = AngleWithinRange(angleAbs, (90f - m_ShapeParams.splineData.z), (90f + m_ShapeParams.splineData.z));
            if (corner)
            {
                float2 rdir = math.normalize(icp.position - pcp.position);
                int ct = CalculateCorner(index, angle, rdir, idir);
                // Check if we have a valid Sprite.
                if (ct > 0)
                {
                    JobSpriteInfo cspr = GetCornerSpriteInfo(ct);
                    return AttachCorner(index, ct, cspr, ref newPoints, ref activePoint);
                }
            }

            return false;
        }

        void TessellateCorners()
        {

            for (int corner = 1; corner <= kCornerTypeInnerBottomRight; ++corner)
            {
                JobSpriteInfo isi = GetCornerSpriteInfo(corner);
                if (isi.metaInfo.x == 0)
                    continue;

                int ic = 0;
                int vc = 0;
                Vector3 pos = m_PosArray[ic];
                Vector2 uv0 = m_Uv0Array[ic];
                bool ccw = (corner <= kCornerTypeOuterBottomRight);
                int vertexArrayCount = m_VertexArrayCount;

                for (int i = 0; i < m_CornerCount; ++i)
                {
                    JobCornerInfo isc = m_Corners[i];
                    if (isc.cornerData.x == corner)
                    {
                        // Vertices.
                        pos.x = isc.top.x;
                        pos.y = isc.top.y;
                        uv0.x = (CornerTextureCoordinate(corner, 1).x * isi.uvInfo.z) + isi.uvInfo.x;
                        uv0.y = (CornerTextureCoordinate(corner, 1).y * isi.uvInfo.w) + isi.uvInfo.y;
                        m_PosArray[m_VertexArrayCount] = pos;
                        m_Uv0Array[m_VertexArrayCount++] = uv0;


                        pos.x = isc.right.x;
                        pos.y = isc.right.y;
                        uv0.x = (CornerTextureCoordinate(corner, 0).x * isi.uvInfo.z) + isi.uvInfo.x;
                        uv0.y = (CornerTextureCoordinate(corner, 0).y * isi.uvInfo.w) + isi.uvInfo.y;
                        m_PosArray[m_VertexArrayCount] = pos;
                        m_Uv0Array[m_VertexArrayCount++] = uv0;

                        pos.x = isc.left.x;
                        pos.y = isc.left.y;
                        uv0.x = (CornerTextureCoordinate(corner, 3).x * isi.uvInfo.z) + isi.uvInfo.x;
                        uv0.y = (CornerTextureCoordinate(corner, 3).y * isi.uvInfo.w) + isi.uvInfo.y;
                        m_PosArray[m_VertexArrayCount] = pos;
                        m_Uv0Array[m_VertexArrayCount++] = uv0;

                        pos.x = isc.bottom.x;
                        pos.y = isc.bottom.y;
                        uv0.x = (CornerTextureCoordinate(corner, 2).x * isi.uvInfo.z) + isi.uvInfo.x;
                        uv0.y = (CornerTextureCoordinate(corner, 2).y * isi.uvInfo.w) + isi.uvInfo.y;
                        m_PosArray[m_VertexArrayCount] = pos;
                        m_Uv0Array[m_VertexArrayCount++] = uv0;

                        // Indices.
                        m_IndexArray[m_IndexArrayCount++] = (ushort)(vc + 0);
                        m_IndexArray[m_IndexArrayCount++] = (ushort)(vc + (ccw ? 1 : 3));
                        m_IndexArray[m_IndexArrayCount++] = (ushort)(vc + (ccw ? 3 : 1));

                        m_IndexArray[m_IndexArrayCount++] = (ushort)(vc + 0);
                        m_IndexArray[m_IndexArrayCount++] = (ushort)(vc + (ccw ? 3 : 2));
                        m_IndexArray[m_IndexArrayCount++] = (ushort)(vc + (ccw ? 2 : 3));

                        vc = vc + 4;
                        ic = ic + 6;
                    }
                }

                if (m_TanArray.Length > 1)
                {
                    for (int i = vertexArrayCount; i < m_VertexArrayCount; ++i)
                        m_TanArray[i] = new Vector4(1.0f, 0, 0, -1.0f);
                }

                // Geom Data
                if (ic > 0 && vc > 0)
                {
                    var geom = m_GeomArray[m_GeomArrayCount];
                    geom.geomIndex = m_GeomArrayCount;
                    geom.indexCount = ic;
                    geom.vertexCount = vc;
                    geom.spriteIndex = m_SpriteInfos.Length + (corner - 1);
                    m_GeomArray[m_GeomArrayCount++] = geom;
                }
            }
        }

        #endregion

        #region Fast Optimizations

        bool AreCollinear(float2 a, float2 b, float2 c, float t)
        {
            float ax = (a.y - b.y) * (a.x - c.x);
            float bx = (a.y - c.y) * (a.x - b.x);
            float aa = math.abs(ax - bx);
            return aa < t;
        }

        // Check if points are co linear and reduce.
        void OptimizePoints(float tolerance, ref NativeArray<float2> pointSet, ref int pointCount)
        {
            int kMinimumPointsRequired = 8;
            if (pointCount < kMinimumPointsRequired)
                return;

            int optimizedColliderPointCount = 0;
            int endColliderPointCount = pointCount - 2;
            bool val = true;
            m_TempPoints[0] = pointSet[0];
            for (int i = 0; i < endColliderPointCount; ++i)
            {
                int j = i;
                float2 v0 = pointSet[i];
                float2 v1 = pointSet[i + 1];
                float2 v2 = pointSet[i + 2];
                val = true;
                while (val && j < endColliderPointCount)
                {
                    val = AreCollinear(v0, v1, v2, tolerance);
                    if (false == val)
                    {
                        m_TempPoints[++optimizedColliderPointCount] = v1;
                        i = j;
                        break;
                    }
                    j++;
                    v1 = pointSet[j + 1];
                    v2 = pointSet[j + 2];
                }
            }
            m_TempPoints[++optimizedColliderPointCount] = pointSet[endColliderPointCount];
            m_TempPoints[++optimizedColliderPointCount] = pointSet[endColliderPointCount + 1];
            if (isCarpet)
                m_TempPoints[++optimizedColliderPointCount] = pointSet[0];
            pointCount = optimizedColliderPointCount + 1;
            for (int i = 0; i < pointCount; ++i)
                pointSet[i] = m_TempPoints[i];
        }

        #endregion

        #region Collider Specific.
        void AttachCornerToCollider(JobSegmentInfo isi, float pivot, ref NativeArray<float2> colliderPoints, ref int colliderPointCount)
        {
            float2 zero = new float2(0, 0);
            int cornerIndex = isi.spInfo.x + 1;
            for (int i = 0; i < m_CornerCount; ++i)
            {
                JobCornerInfo isc = m_Corners[i];
                if (cornerIndex == isc.cornerData.y)
                {
                    float2 cp = zero;
                    float2 v0 = zero;
                    if (isc.cornerData.x > kCornerTypeOuterBottomRight)
                        v0 = isc.top;
                    else
                        v0 = isc.bottom;

                    float2 v2 = zero;
                    if (isc.cornerData.x > kCornerTypeOuterBottomRight)
                        v2 = isc.bottom;
                    else
                        v2 = isc.top;
                    cp = (v2 - v0) * pivot;
                    cp = (v2 + cp + v0 + cp) * 0.5f;
                    colliderPoints[colliderPointCount++] = cp;
                    break;
                }
            }
        }

        float2 UpdateCollider(JobSegmentInfo isi, JobSpriteInfo ispr, NativeArray<JobShapeVertex> vertices, int count, ref NativeArray<float2> colliderPoints, ref int colliderPointCount)
        {
            float2 zero = new float2(0, 0);
            float pivot = 0.5f - ispr.metaInfo.y;
            pivot = pivot + colliderPivot;
            AttachCornerToCollider(isi, pivot, ref colliderPoints, ref colliderPointCount);

            float2 cp = zero;
            float2 v0 = zero;
            float2 v2 = zero;

            for (int k = 0; k < count; k = k + 4)
            {
                v0 = vertices[k].pos;
                v2 = vertices[k + 2].pos;
                cp = (v2 - v0) * pivot;
                colliderPoints[colliderPointCount++] = (v2 + cp + v0 + cp) * 0.5f;
            }

            float2 v1 = vertices[count - 1].pos;
            float2 v3 = vertices[count - 3].pos;
            cp = (v1 - v3) * pivot;
            colliderPoints[colliderPointCount++] = (v1 + cp + v3 + cp) * 0.5f;
            return cp;
        }

        void TrimOverlaps()
        {
            int kMinimumPointTolerance = 4;
            if (m_ColliderPointCount < kMinimumPointTolerance)
                return;
            int trimmedPointCount = 0;
            int i = 0;
            int kColliderPointCountClamped = m_ColliderPointCount / 2;
            int kSplineDetailClamped = math.clamp(splineDetail * 3, 0, 8);
            int kNeighbors = kSplineDetailClamped > kColliderPointCountClamped ? kColliderPointCountClamped : kSplineDetailClamped;
            // Debug.Log(kSplineDetailClamped + " : " + kNeighbors + " = " + m_ColliderPointCount);

            if (!isCarpet)
                m_TempPoints[trimmedPointCount++] = m_ColliderPoints[0];

            while (i < m_ColliderPointCount)
            {
                int h = (i > 0) ? (i - 1) : (m_ColliderPointCount - 1);
                bool noIntersection = true;
                float2 v0 = m_ColliderPoints[h];
                float2 v1 = m_ColliderPoints[i];

                for (int n = kNeighbors; n > 1; --n)
                {
                    int j = (i + n - 1) % m_ColliderPointCount;
                    int k = (i + n) % m_ColliderPointCount;
                    if (k == 0 || i == 0)
                        continue;

                    float2 v2 = m_ColliderPoints[j];
                    float2 v3 = m_ColliderPoints[k];
                    float2 vx = v0 - v3;

                    if (math.abs(math.length(vx)) < kEpsilon)
                        break;

                    float2 vi = v0;

                    bool overLaps = LineIntersection(kEpsilonRelaxed, v0, v1, v2, v3, ref vi);
                    if (overLaps && IsPointOnLines(kEpsilonRelaxed, v0, v1, v2, v3, vi))
                    {
                        // Debug.Log(v0 + " = " + v1 + " : " + v2 + " = " + v3 + " & " + h + " = " + i + " : " + j + " = " + k + " => " + vi + " : " + n);
                        noIntersection = false;
                        m_TempPoints[trimmedPointCount++] = vi;
                        i = i + n;
                        break;
                    }
                }

                if (noIntersection)
                {
                    m_TempPoints[trimmedPointCount++] = v1;
                    i = i + 1;
                }
            }
            for (; i < m_ColliderPointCount; ++i)
                m_TempPoints[trimmedPointCount++] = m_ColliderPoints[i];

            for (int j = 0; j < trimmedPointCount; ++j)
                m_ColliderPoints[j] = m_TempPoints[j];

            // Check intersection of first line Segment and last.
            float2 vin = m_ColliderPoints[0];
            LineIntersection(kEpsilonRelaxed, m_ColliderPoints[0], m_ColliderPoints[1], m_ColliderPoints[trimmedPointCount - 1], m_ColliderPoints[trimmedPointCount - 2], ref vin);
            m_ColliderPoints[0] = vin;

            m_ColliderPointCount = trimmedPointCount;
        }

        void OptimizeCollider()
        {
            if (hasCollider)
            {
                if (kColliderQuality > 0)
                {                    
                    if (kOptimizeCollider > 0)
                    { 
                        OptimizePoints(kColliderQuality, ref m_ColliderPoints, ref m_ColliderPointCount);
                        TrimOverlaps();
                    }
                    m_ColliderPoints[m_ColliderPointCount++] = new float2(0, 0);
                    m_ColliderPoints[m_ColliderPointCount++] = new float2(0, 0);
                }
                if (m_ColliderPointCount <= 2)
                {
                    for (int i = 0; i < m_TessPointCount; ++i)
                        m_ColliderPoints[i] = m_TessPoints[i];
                    m_ColliderPoints[m_TessPointCount] = new float2(0, 0);
                    m_ColliderPoints[m_TessPointCount + 1] = new float2(0, 0);
                    m_ColliderPointCount = m_TessPointCount + 2;
                }
            }
        }

        #endregion

        #region Entry, Exit Points.

        public void Prepare(UnityEngine.U2D.SpriteShapeController controller, SpriteShapeParameters shapeParams, int maxArrayCount, NativeArray<ShapeControlPoint> shapePoints, NativeArray<SpriteShapeMetaData> metaData, AngleRangeInfo[] angleRanges, Sprite[] segmentSprites, Sprite[] cornerSprites)
        {
            // Prepare Inputs.
            PrepareInput(shapeParams, maxArrayCount, shapePoints, controller.optimizeGeometry, controller.autoUpdateCollider, controller.optimizeCollider, controller.colliderOffset, controller.colliderDetail);
            PrepareSprites(segmentSprites, cornerSprites);
            PrepareAngleRanges(angleRanges);
            PrepareControlPoints(shapePoints, metaData);

            // Generate Intermediates.
            GenerateContour();
            TessellateContour();
        }

        public void Execute()
        {
            // BURST
            GenerateSegments();
            TessellateSegments();
            TessellateCorners();
            CalculateBoundingBox();
            CalculateTexCoords();
            OptimizeCollider();
        }

        // Only needed if Burst is disabled.
        // [BurstDiscard]
        public void Cleanup()
        {
            SafeDispose(m_Corners);
            SafeDispose(m_CornerSpriteInfos);
            SafeDispose(m_SpriteInfos);
            SafeDispose(m_AngleRanges);
            SafeDispose(m_Segments);
            SafeDispose(m_ControlPoints);
            SafeDispose(m_ContourPoints);
            SafeDispose(m_AreaIndices);
            SafeDispose(m_AreaVertices);
            SafeDispose(m_AreaInputs);
            SafeDispose(m_TempPoints);
            SafeDispose(m_GeneratedControlPoints);
            SafeDispose(m_SpriteIndices);

            SafeDispose(m_TessPoints);
            SafeDispose(m_VertexData);
            SafeDispose(m_OutputVertexData);
            SafeDispose(m_CornerCoordinates);
        }

        #endregion

    }
};
