using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Tauntastic.WebBrowser.UI
{
    /// <summary>
    /// Shows a <see cref="WebBrowserView"/> on a uGUI RawImage and forwards mouse input to the page (needs an EventSystem in the scene). A click gives the page keyboard focus.
    /// Add it next to the RawImage; it finds the view on the same GameObject or, if given, in <see cref="_view"/>.
    /// </summary>
    [RequireComponent(typeof(RawImage))]
    [AddComponentMenu("Tauntastic/Web Browser Pointer Input")]
    public class WebBrowserPointerInput : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerMoveHandler, IScrollHandler
    {
        [Tooltip("The view to show and control. Leave empty to use the one on this GameObject.")]
        [SerializeField] private WebBrowserView _view;

        private RawImage _image;
        private static WebBrowserView _focused;

        private void Awake()
        {
            _image = GetComponent<RawImage>();
            if (_view == null)
                _view = GetComponent<WebBrowserView>();
        }

        private void Start()
        {
            if (_view != null)
                _image.texture = _view.Texture;
        }

        private void LateUpdate()
        {
            // The texture object stays the same while the viewport changes size, but rebind in case the view was recreated.
            if (_view != null && _image.texture != _view.Texture)
                _image.texture = _view.Texture;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            if (_view == null || !TryGetPagePosition(eventData, out Vector2 position))
                return;

            if (_focused != null && _focused != _view)
                _focused.HasFocus = false;
            _focused = _view;
            _view.HasFocus = true;
            _view.Screen.MouseDown(position.x, position.y, ButtonOf(eventData), Mathf.Max(1, eventData.clickCount));
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            if (_view != null && TryGetPagePosition(eventData, out Vector2 position))
                _view.Screen.MouseUp(position.x, position.y, ButtonOf(eventData), Mathf.Max(1, eventData.clickCount));
        }

        public void OnPointerMove(PointerEventData eventData)
        {
            if (_view != null && TryGetPagePosition(eventData, out Vector2 position))
                _view.Screen.MouseMove(position.x, position.y);
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (_view != null && TryGetPagePosition(eventData, out Vector2 position))
                _view.Screen.Scroll(position.x, position.y, -eventData.scrollDelta.x * 40f, -eventData.scrollDelta.y * 40f);
        }

        /// <summary>The pointer position in page pixels (from the top left), or false when it is outside the image.</summary>
        private bool TryGetPagePosition(PointerEventData eventData, out Vector2 position)
        {
            position = default;
            var rect = (RectTransform)transform;
            Camera camera = eventData.pressEventCamera != null ? eventData.pressEventCamera : eventData.enterEventCamera;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, eventData.position, camera, out Vector2 local))
                return false;

            float u = (local.x - rect.rect.x) / rect.rect.width;
            float v = (local.y - rect.rect.y) / rect.rect.height;
            if (u < 0f || u > 1f || v < 0f || v > 1f)
                return false;

            position = new Vector2(u * _view.Screen.Width, (1f - v) * _view.Screen.Height);
            return true;
        }

        private static int ButtonOf(PointerEventData eventData)
        {
            return eventData.button == PointerEventData.InputButton.Right ? 1 : eventData.button == PointerEventData.InputButton.Middle ? 2 : 0;
        }
    }
}
