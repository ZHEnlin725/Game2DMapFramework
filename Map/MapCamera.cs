using System;
using FairyGUI;
using UnityEngine;

[RequireComponent(typeof(Camera))]
public class MapCamera : MonoBehaviour
{
    // 相机可超出地图的范围
    public float expand = 0.5f,
        // 相机弹性边界     // 超过弹性边界时的回弹速度
        elasticEdge = 1,
        elasticSpeed = 5,
        // 相机平移速度缩减比例  // 相机平移灵敏度
        linearDrag = 0.05f,
        sensitivity = 0.01f;

    public float maxSwipeDist = 150;

    public bool draggable = true;

    public bool useInertiaDuration;
    public float inertiaDuration;
    public Vector2 followOffset;

    public event Action<Vector3> OnPositionChanged;

    private float _inertiaElapse;

    private Vector3 _planeEulerAngles = Vector3.zero;

    private Camera _cameraCache;
    private Transform _camTranCache;
    private GameObject _camGoCache;

    private Plane _plane;
    private Bounds _planeBounds = SmallBox;
    private Bounds _cameraViewBounds = SmallBox;

    private bool _isMoving;

    [SerializeField] private float _minOrthgraphicSize = 3, _maxOrthgraphicSize = 6;

    public Camera CameraCache
    {
        get
        {
            if (_cameraCache == null)
                _cameraCache = GetComponent<Camera>();
            return _cameraCache;
        }
    }

    public Transform CamTranCache
    {
        get
        {
            if (_camTranCache == null)
                _camTranCache = transform;
            return _camTranCache;
        }
    }

    public GameObject CamGoCache
    {
        get
        {
            if (_camGoCache == null)
                _camGoCache = gameObject;
            return _camGoCache;
        }
    }

    public float DistToPlane
    {
        get
        {
            var origin = CamTranCache.position;
            var direction = CamTranCache.forward;
            if (!_plane.Raycast(new Ray(origin, direction), out var enter))
                return float.NaN;

            var magnitude = (direction * enter).magnitude;
            return magnitude;
        }

        set
        {
            var clamp = Mathf.Clamp(value, 5, 30);
            var origin = CamTranCache.position;
            var direction = CamTranCache.forward;
            if (!_plane.Raycast(new Ray(origin, direction), out var enter))
                return;

            if (!DistIsLegal(enter))
                // Debug.Log("not illegal");
                return;

            var point = origin + direction * enter;
            CamTranCache.position = point - direction * clamp;
        }
    }

    public Vector3 EulerAngles
    {
        get => CamTranCache.eulerAngles;
        set
        {
            if (EulerAnglesIsLegal(value))
                CamTranCache.eulerAngles = value;
        }
    }

    public float FieldOfView
    {
        get => CameraCache.fieldOfView;
        set
        {
            if (FieldOfViewIsLegal(value))
                CameraCache.fieldOfView = value;
        }
    }

    public float OrthographicSize
    {
        get => CameraCache.orthographicSize;
        set
        {
            var clamp = Mathf.Clamp(value, _minOrthgraphicSize, _maxOrthgraphicSize);
            if (OrthographicSizeIsLegal(clamp))
                CameraCache.orthographicSize = clamp;
        }
    }

    public bool ManualUpdate { get; set; }

    public bool IsMoving => _isMoving;

    public bool IsSwiping => _isSwiping;

    public void DoUpdate()
    {
        UpdateViewBounds();

        UpdateTranslationMotion();

        EdgeSpringBack(_cameraViewBounds);
    }

    private void Update()
    {
        if (ManualUpdate) return;

        DoUpdate();
    }

#if UNITY_EDITOR
    [SerializeField] private bool drawGizmos;

    private void OnDrawGizmos()
    {
        if (drawGizmos && CameraCache != null)
        {
            Gizmos.color = Color.blue;
            Gizmos.DrawWireCube(_cameraViewBounds.center, _cameraViewBounds.size);
            
            Gizmos.color = Color.red;
            Gizmos.DrawWireCube(_planeBounds.center, _planeBounds.size);
        }
    }
#endif

    public void UpdateMapBoundsEulerAngles(Bounds mapBounds, Vector3 planeEulerAngles)
    {
        _planeBounds = new Bounds(mapBounds.center, mapBounds.size);
        _planeEulerAngles = planeEulerAngles;
        var planeNormal = Quaternion.Euler(_planeEulerAngles) * Vector3.up;
        _plane = new Plane(planeNormal, _planeBounds.center);
    }

    public void UpdateViewBounds()
    {
        var camPosition = CamTranCache.position;
        var camRotation = CamTranCache.rotation;
        _cameraViewBounds = !CameraCache.orthographic
            ? EvaluatePerspectiveBounds(CameraCache, camPosition, camRotation, _plane)
            : EvaluateOrthographicBounds(CameraCache, camPosition, camRotation, _plane);
        _planeBounds.Encapsulate(_cameraViewBounds);
    }

    public void StopTranslation()
    {
        _transition = Vector2.zero;
    }

    public void Follow(Vector3 targetPosition, float lerp = 1f)
    {
        if (!isActiveAndEnabled) return;
        // if (_cachedTweener >= 0)
        // {
        //     // LeanTween.cancel(_cachedTweener);
        //     _cachedTweener = -1;
        // }
        if (_cachedTweener != null)
        {
            _cachedTweener.Kill();
            _cachedTweener = null;
        }

        var oldPosition = CamTranCache.position;
        targetPosition.x -= followOffset.x;
        targetPosition.y -= followOffset.y;
        var newPosition = Vector3.Lerp(oldPosition, GetCameraPosition(targetPosition), lerp);
        if (!Mathf.Approximately(Vector3.Distance(newPosition, oldPosition), 0f))
        {
            CamTranCache.position = newPosition;
            OnPositionChanged?.Invoke(newPosition);
        }
    }

    private GTweener _cachedTweener;

    public void MoveTo(Vector3 targetPosition, float duration, Action onComplete = null)
    {
        // GTween.Kill();
        // if (_cachedTweener >= 0) LeanTween.cancel(_cachedTweener);
        if (_cachedTweener != null) _cachedTweener.Kill();
        var position = GetCameraPosition(targetPosition);

        void Complete()
        {
            // _cachedTweener = -1;
            _cachedTweener = null;
            if (onComplete != null) onComplete();
        }

        var tweener = GTween.To(CamTranCache.position, position, duration);
        tweener.OnComplete(Complete);
        _cachedTweener = tweener;
        // _cachedTweener = LeanTween.move(camGoCache, position, duration).setOnComplete(Complete).id;
        // return _cachedTweener;
    }

    public void SetOrthgraphicSizeMinMax(float a, float b)
    {
        _minOrthgraphicSize = Mathf.Min(a, b);
        _maxOrthgraphicSize = Mathf.Max(a, b);
    }

    public Vector3 GetViewCenter() =>
        _cameraViewBounds.center;

    public Bounds GetViewBounds() =>
        _cameraViewBounds;

    public Vector3 GetCameraPosition(Vector3 targetPosition)
    {
        var position = CamTranCache.position;
        var offset = position - _cameraViewBounds.center;
        return ClampPosition(_cameraViewBounds, targetPosition) + offset;
    }

    public bool Arrival(Vector3 targetPosition, float epsilon = float.Epsilon)
    {
        var position = GetCameraPosition(targetPosition);
        return Vector3.Distance(CamTranCache.position, position) < epsilon;
    }

    public bool InFieldOfView(Bounds bounds, float shrinkX = 0f, float shrinkY = 0f, bool transform = false)
    {
        var viewBounds = new Bounds(_cameraViewBounds.center,
            (_cameraViewBounds.extents - new Vector3(shrinkX, shrinkY)) * 2);
        if (transform)
        {
            var matrix = InverseRotate(_planeEulerAngles);
            bounds = TransformBounds(matrix, bounds);
            return IntersectsXZ(TransformBounds(matrix, viewBounds), bounds);
        }

        return IntersectsXY(viewBounds, bounds);
    }

    public bool InViewport(Vector3 position, float shrinkX = 0f, float shrinkY = 0f)
    {
        var point = CameraCache.WorldToViewportPoint(position);
        if (point.z < 0) return false;
        if (point.z > CameraCache.farClipPlane) return false;
        shrinkX = Mathf.Clamp01(Mathf.Abs(shrinkX)) * 0.5f;
        shrinkY = Mathf.Clamp01(Mathf.Abs(shrinkY)) * 0.5f;
        return point.x > shrinkX && point.x < 1 - shrinkX &&
               point.y > shrinkY && point.y < 1 - shrinkY;
    }

    private bool _isSwiping;
    private Vector2 _transition;
    private Vector2 _snapshot;
    private Vector2 _beginPoint;
    private DateTime _begineTime;

    public void OnBeginSwipe(Vector2 finger)
    {
        if (!draggable) return;

        _isSwiping = true;
        _beginPoint = finger;
        _transition = Vector2.zero;
        _begineTime = DateTime.Now;
    }

    public void OnEndSwipe(Vector2 finger)
    {
        _isSwiping = false;
        if (!draggable) return;
        var delta = finger - _beginPoint;
        var milliseconds = (DateTime.Now - _begineTime).TotalMilliseconds;
        if (milliseconds < 150)
        {
            _transition = -delta * sensitivity / 10;
        }

        _snapshot = _transition;
        _inertiaElapse = 0;
    }

    public void OnSwipe(Vector2 finger, Vector2 direction, float dist)
    {
        if (!draggable)
        {
            _isSwiping = false;
            return;
        }

        if (!_isSwiping) return;

        _transition = -direction.normalized * (Mathf.Min(dist, maxSwipeDist) * sensitivity);
    }

    public void OnZoom(float zoomDelta)
    {
        if (CameraCache == null) return;
        if (CameraCache.orthographic)
        {
            OrthographicSize -= zoomDelta;
        }
        else
        {
            DistToPlane -= zoomDelta;
        }
    }

    private void Translation(Bounds viewBounds, Vector2 transition)
    {
        if (CameraCache == null || CamTranCache == null)
        {
            return;
        }

        var position = CamTranCache.position;
        var offset = position - viewBounds.center;
        var newPosition = ClampPosition(_cameraViewBounds, position + new Vector3(transition.x, transition.y), offset);
        if (!Mathf.Approximately(Vector3.Distance(newPosition, position), 0f))
        {
            CamTranCache.position = newPosition;
            OnPositionChanged?.Invoke(newPosition);
        }
    }

    private Vector3 ClampPosition(Bounds viewBounds, Vector3 worldPosition, Vector3 offset = default)
    {
        var world2LocalRotate = InverseRotate(_planeEulerAngles);
        var pointInPlane = world2LocalRotate.MultiplyPoint(worldPosition);
        var expandBounds = new Bounds(_planeBounds.center,
            _planeBounds.size + Vector3.one * expand);
        var clampPositionXZ = ClampPositionXZ(pointInPlane,
            TransformBounds(world2LocalRotate, expandBounds),
            TransformBounds(world2LocalRotate, viewBounds),
            world2LocalRotate.MultiplyVector(offset));
        return world2LocalRotate.inverse.MultiplyPoint(clampPositionXZ);
    }

    private void UpdateTranslationMotion()
    {
        if (!(Mathf.Abs(_transition.x) > float.Epsilon) && !(Mathf.Abs(_transition.y) > float.Epsilon))
        {
            _isMoving = false;
            return;
        }

        _isMoving = true;

        Translation(_cameraViewBounds, _transition);

        if (IsSwiping)
        {
            _transition = Vector2.zero;
            return;
        }

        if (useInertiaDuration)
        {
            if (inertiaDuration == 0) inertiaDuration = 0.5f;
            _inertiaElapse += Time.deltaTime;
            _transition = Vector2.Lerp(_snapshot, Vector2.zero, _inertiaElapse / inertiaDuration);
        }
        else
        {
            _transition = Vector2.Lerp(_transition, Vector2.zero, linearDrag);
            _snapshot = _transition;
        }
    }

    private void EdgeSpringBack(Bounds viewBounds)
    {
        if (_isSwiping) return;

        var spring = false;

        var direction = Vector3.zero;

        var world2LocalRotate = InverseRotate(_planeEulerAngles);
        var planeBounds = TransformBounds(world2LocalRotate, _planeBounds);
        viewBounds = TransformBounds(world2LocalRotate, viewBounds);

        if (viewBounds.max.x > planeBounds.max.x - elasticEdge)
        {
            spring = true;
            direction += Vector3.left;
        }

        if (viewBounds.max.z > planeBounds.max.z - elasticEdge)
        {
            spring = true;
            direction += Vector3.back;
        }

        if (viewBounds.min.x < planeBounds.min.x + elasticEdge)
        {
            spring = true;
            direction += Vector3.right;
        }

        if (viewBounds.min.z < planeBounds.min.z + elasticEdge)
        {
            spring = true;
            direction += Vector3.forward;
        }

        if (spring)
        {
            // _transition = Vector2.zero;
            direction = world2LocalRotate.inverse.MultiplyVector(direction);
            CamTranCache.position += direction.normalized * (elasticSpeed * Time.deltaTime);
        }
    }

    private bool EulerAnglesIsLegal(Vector3 eulerAngles)
    {
        var position = CamTranCache.position;
        var rotation = Quaternion.Euler(eulerAngles);
        var isLegal = CameraCache.orthographic
            ? ViewBoundsIsInMapBoundsOrthographic(CameraCache.orthographicSize, position, rotation)
            : ViewBoundsIsInMapBoundsPerspective(CameraCache.fieldOfView, position, rotation);
        return isLegal;
    }

    private bool OrthographicSizeIsLegal(float orithographicSize)
    {
        if (CameraCache == null) return false;
        return ViewBoundsIsInMapBoundsOrthographic(orithographicSize, CamTranCache.position,
            CamTranCache.rotation);
    }

    private bool FieldOfViewIsLegal(float fieldOfView)
    {
        if (CameraCache == null) return false;
        return ViewBoundsIsInMapBoundsPerspective(fieldOfView, CamTranCache.position, CamTranCache.rotation);
    }

    private bool DistIsLegal(float dist)
    {
        if (CameraCache == null) return false;
        return ViewBoundsIsInMapBoundsPerspective(CameraCache.fieldOfView,
            CamTranCache.position - CamTranCache.forward * dist,
            CamTranCache.rotation);
    }

    private bool ViewBoundsIsInMapBoundsPerspective(float fieldOfView, Vector3 position, Quaternion rotation)
    {
        var world2LocalRotate = InverseRotate(_planeEulerAngles);
        var bounds = TransformBounds(world2LocalRotate, EvaluatePerspectiveBounds(fieldOfView,
            getHorizontalFOVDeg(fieldOfView, CameraCache.aspect), position, rotation, _plane));
        var planeBounds = TransformBounds(world2LocalRotate, _planeBounds);
        return planeBounds.max.x >= bounds.max.x
               && planeBounds.max.z >= bounds.max.z
               && planeBounds.min.x <= bounds.min.x
               && planeBounds.min.z <= bounds.min.z;
    }

    private bool ViewBoundsIsInMapBoundsOrthographic(float orathographicSize, Vector3 position, Quaternion rotation)
    {
        var world2LocalRotate = InverseRotate(_planeEulerAngles);
        var bounds = TransformBounds(world2LocalRotate,
            EvaluateOrthographicBounds(orathographicSize, orathographicSize * CameraCache.aspect, position,
                rotation, _plane));
        var planeBounds = TransformBounds(world2LocalRotate, _planeBounds);
        return planeBounds.max.x >= bounds.max.x
               && planeBounds.max.z >= bounds.max.z
               && planeBounds.min.x <= bounds.min.x
               && planeBounds.min.z <= bounds.min.z;
    }

    #region Static Methods

    private static readonly Bounds SmallBox = new Bounds(Vector3.negativeInfinity, Vector3.negativeInfinity);

    private static Bounds EvaluatePerspectiveBounds(Camera camera, Vector3 position, Quaternion rotation, Plane plane)
    {
        var fieldOfView = camera.fieldOfView;
        return EvaluatePerspectiveBounds(fieldOfView, getHorizontalFOVDeg(fieldOfView, camera.aspect),
            position, rotation, plane);
    }

    private static Bounds EvaluateOrthographicBounds(Camera camera, Vector3 position, Quaternion rotation,
        Plane plane)
    {
        var orthographicSize = camera.orthographicSize;
        return EvaluateOrthographicBounds(orthographicSize, orthographicSize * camera.aspect,
            position, rotation, plane);
    }

    private static Bounds EvaluatePerspectiveBounds(float vFOV, float hFOV, Vector3 position, Quaternion rotation,
        Plane plane)
    {
        Vector3 forward = rotation * Vector3.forward, right = rotation * Vector3.right, up = rotation * Vector3.up;

        var bounds = SmallBox;

        for (var sign = -1; sign <= 1; sign += 2)
        {
            var vector = Quaternion.AngleAxis(sign * vFOV * 0.5f, right) * forward;
            if (plane.Raycast(new Ray(position, vector), out var enter))
                bounds.Encapsulate(position + vector * enter);
        }

        for (var sign = -1; sign <= 1; sign += 2)
        {
            var vector = Quaternion.AngleAxis(sign * hFOV * 0.5f, up) * forward;
            if (plane.Raycast(new Ray(position, vector), out var enter))
                bounds.Encapsulate(position + vector * enter);
        }

        return bounds;
    }

    private static Bounds EvaluateOrthographicBounds(float height, float width, Vector3 position,
        Quaternion rotation, Plane plane)
    {
        Vector3 forward = rotation * Vector3.forward, right = rotation * Vector3.right, up = rotation * Vector3.up;

        var bounds = SmallBox;

        for (var x = -1; x <= 1; x += 2)
        {
            for (var y = -1; y <= 1; y += 2)
            {
                var offset = right * x * width + up * y * height;
                var point = position + offset;
                if (plane.Raycast(new Ray(point, forward), out var enter))
                    bounds.Encapsulate(point + forward * enter);
            }
        }

        return bounds;
    }

    private static float getHorizontalFOVRad(float fovRad, float aspect) =>
        2 * Mathf.Atan(Mathf.Tan(fovRad * 0.5f) * aspect);

    private static float getHorizontalFOVDeg(float fovDeg, float aspect) =>
        getHorizontalFOVRad(fovDeg * Mathf.Deg2Rad, aspect) * Mathf.Rad2Deg;

    public static Bounds TransformBounds(Matrix4x4 matrix, Bounds bounds)
    {
        // 包围盒经旋转后需按八个角点重新求 AABB，直接变换 extents 只在轴对齐旋转下正确。
        var ext = bounds.extents;
        var min = Vector3.positiveInfinity;
        var max = Vector3.negativeInfinity;

        for (var x = -1; x <= 1; x += 2)
        for (var y = -1; y <= 1; y += 2)
        for (var z = -1; z <= 1; z += 2)
        {
            var corner = matrix.MultiplyPoint3x4(bounds.center + new Vector3(ext.x * x, ext.y * y, ext.z * z));
            min = Vector3.Min(min, corner);
            max = Vector3.Max(max, corner);
        }

        return new Bounds(matrix.MultiplyPoint(bounds.center), max - min);
    }

    private static Matrix4x4 InverseRotate(Vector3 localEulerAngles) =>
        Matrix4x4.Rotate(Quaternion.Euler(localEulerAngles)).inverse;
    
    private static Vector3 ClampPositionXZ(Vector3 position, Bounds maxBounds, Bounds objBounds,
        Vector3 offset = default)
    {
        Vector3 constraintMax = maxBounds.max - objBounds.extents + offset,
            constraintMin = maxBounds.min + objBounds.extents + offset;

        float xClamp = Mathf.Clamp(position.x, constraintMin.x, constraintMax.x),
            zClamp = Mathf.Clamp(position.z, constraintMin.z, constraintMax.z);

        return new Vector3(xClamp, position.y, zClamp);
    }

    private static bool IntersectsXY(Bounds x, Bounds y) =>
        (double)x.min.x <= (double)y.max.x &&
        (double)x.max.x >= (double)y.min.x &&
        ((double)x.min.y <= (double)y.max.y &&
         (double)x.max.y >= (double)y.min.y);

    private static bool IntersectsXZ(Bounds x, Bounds y) =>
        (double)x.min.x <= (double)y.max.x &&
        (double)x.max.x >= (double)y.min.x &&
        ((double)x.min.z <= (double)y.max.z &&
         (double)x.max.z >= (double)y.min.z);

    #endregion
}