using UnityEngine;

namespace ShipEditor.UI
{
	[RequireComponent(typeof(Canvas))]
	public class CanvasTransformHelper : MonoBehaviour
	{
        [SerializeField] private ShipView _shipView;
        
        private Canvas _canvas;
		private Camera _camera;
		private RectTransform _rectTransform;

		private void Awake()
		{
			_camera = Camera.main;
			_canvas = GetComponent<Canvas>();
			_rectTransform = GetComponent<RectTransform>();
		}

        public float GetShipRotation() => _shipView.transform.localEulerAngles.z - _camera.transform.localEulerAngles.z;

        public Vector2 GetCellSize() => GetUnitSquare() *_shipView.Scale;

        public Vector2 GetUnitSquare()
		{
			var screenPointZero = _camera.WorldToScreenPoint(Vector3.zero);
			var screenPointOne = _camera.WorldToScreenPoint(_camera.transform.up + _camera.transform.right);
			var canvasRect = _rectTransform.rect;
			var scale = new Vector2(canvasRect.width / Screen.width, canvasRect.height / Screen.height);

			return new Vector2(screenPointOne.x - screenPointZero.x, screenPointOne.y - screenPointZero.y) * scale;
		}

		public Vector3 ScreenToWorld(Vector2 position)
		{
			return _camera.ScreenToWorldPoint(position);
		}
	}
}
