﻿using UnityEngine;

namespace ShipEditor.UI
{
	[RequireComponent(typeof(Camera))]
	public class CameraController : MonoBehaviour
	{
		[SerializeField] private RectTransform _focus;
        [SerializeField] private float _smoothTime = 0.1f;
        [SerializeField] private float _minSpeed = 5.0f;

        private const float _epsilon = 0.01f;

        private Camera _camera;
		private float _orthographicSize;
        private float _zoomVelocity;
        private int _screenWidth;
		private int _screenHeight;
        private Vector2 _focusPoint;
		private Vector2 _focusVelocity;
        private Vector2 _position;
        private float _rotation;
        private float _angularVelocity;

        public float Width => Height * Aspect;
		public float Height => 2*_orthographicSize;
		public float Aspect => _screenHeight > 0 ? (float)_screenWidth / _screenHeight : 1f;
		public float OrthographicSize { get => _camera.orthographicSize; set => _camera.orthographicSize = value; }
        public Vector2 Position { get => _position; set => _position = value; }
        public float Rotation { get => _rotation; set => _rotation = value; }
        public Vector2 FocusOffset => new Vector2((0.5f - _focusPoint.x) * Width, (0.5f - _focusPoint.y) * Height);
		public RectTransform Focus  { get => _focus; set => _focus = value; }

		public float AspectFromFocus
		{
			get
			{
				if (_screenHeight == 0) return 1.0f;
				var focusPoint = (_focus.anchorMin + _focus.anchorMax) / 2;
				var width = _screenWidth * (0.5f - Mathf.Abs(0.5f - Mathf.Clamp01(focusPoint.x)));
				var height = _screenHeight * (0.5f - Mathf.Abs(0.5f - Mathf.Clamp01(focusPoint.y)));
				return width / height;
			}
		}

		private void Awake()
		{
			_camera = GetComponent<Camera>();
			TryUpdateDimensions();
		}

		private void Update()
		{
			if (!TryUpdateDimensions()) return;

			var halfHeight = _orthographicSize;
			var halfWidth = halfHeight * Aspect;

			var x = (1.0f - 2*_focusPoint.x) * halfWidth;
			var y = (1.0f - 2*_focusPoint.y) * halfHeight;

			var left = x - halfWidth;
			var right = x + halfWidth;
			var top = y - halfHeight;
			var bottom = y + halfHeight;

			var matrix = Matrix4x4.Ortho(left, right, top, bottom, _camera.nearClipPlane, _camera.farClipPlane);
			_camera.projectionMatrix = matrix;
		}

		private bool TryUpdateDimensions()
		{
			var dataChanged = false;

			var screenSizeChanged = _screenWidth != Screen.width || _screenHeight != Screen.height;
			if (screenSizeChanged)
			{
				_screenWidth = Screen.width;
				_screenHeight = Screen.height;
				dataChanged = true;
			}

            dataChanged |= UpdateOrthographicSize();
            dataChanged |= UpdatePosition();
            dataChanged |= UpdateFocusPoint();
            dataChanged |= UpdateRotation();

			return dataChanged;
		}

        private bool UpdatePosition()
        {
            var cameraPositon = transform.localPosition;
            var current = (Vector2)cameraPositon;
            if (current == _position) return false;

            var delta = Time.unscaledDeltaTime / _smoothTime;
            var position = current.Lerp(_position, delta, _minSpeed*delta);
            cameraPositon.x = position.x;
            cameraPositon.y = position.y;

            transform.localPosition = cameraPositon;

            return true;
        }

        private bool UpdateFocusPoint()
		{
            var focus = 0.5f * (_focus.anchorMin + _focus.anchorMax);
            if (focus == _focusPoint) return false;

            _focusPoint = Vector2.SmoothDamp(_focusPoint, focus, ref _focusVelocity, _smoothTime, Mathf.Infinity, Time.unscaledDeltaTime);
            
            return true;
		}

		private bool UpdateOrthographicSize()
		{
            if (Mathf.Abs(_orthographicSize - _camera.orthographicSize) <= _epsilon) return false;

            _orthographicSize = Mathf.SmoothDamp(_orthographicSize, _camera.orthographicSize,
                ref _zoomVelocity, _smoothTime, Mathf.Infinity, Time.unscaledDeltaTime);

			return true;
		}

        private bool UpdateRotation()
        {
            var currentRotation = transform.localEulerAngles.z;
            var deltaAngle = Mathf.DeltaAngle(_rotation, currentRotation);
            if (Mathf.Abs(deltaAngle) <= _epsilon) return false;

            currentRotation = Mathf.SmoothDampAngle(currentRotation, _rotation, ref _angularVelocity, _smoothTime,
                Mathf.Infinity, Time.unscaledDeltaTime);

            transform.localEulerAngles = new Vector3(0, 0, currentRotation);
            return true;
        }
    }
}
