using UnityEngine;
using UnityEngine.EventSystems;

namespace Tauntastic.WebBrowser.UI
{
    /// <summary>
    /// Shows a <see cref="WebBrowserView"/> on a 3D object (an in-game monitor, a billboard) and forwards mouse input to the page. The object needs a Renderer and a
    /// MeshCollider (not convex, so the hit has texture coordinates), the camera needs a PhysicsRaycaster, and the scene needs an EventSystem. The page is drawn with the
    /// mesh's UV layout, so use a plane or a quad whose UVs cover the screen. The texture is assigned to the material's main texture.
    /// </summary>
    [RequireComponent(typeof(Renderer))]
    [RequireComponent(typeof(MeshCollider))]
    [AddComponentMenu("Tauntastic/Web Browser Surface Input")]
    public class WebBrowserSurfaceInput : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerMoveHandler, IScrollHandler
    {
        [Tooltip("The view to show and control. Leave empty to use the one on this GameObject.")]
        [SerializeField] private WebBrowserView _view;

        [Tooltip("Name of the material texture property that receives the page (_BaseMap for URP, _MainTex for the built-in pipeline).")]
        [SerializeField] private string _textureProperty = "_BaseMap";

        private Renderer _renderer;
        private MeshCollider _collider;
        private static WebBrowserView _focused;

        private void Awake()
        {
            _renderer = GetComponent<Renderer>();
            _collider = GetComponent<MeshCollider>();
            if (_view == null)
                _view = GetComponent<WebBrowserView>();
        }

        private void Start()
        {
            Bind();
        }

        private void LateUpdate()
        {
            Bind();
        }

        private void Bind()
        {
            if (_view == null)
                return;

            Material material = _renderer.material;
            string property = material.HasProperty(_textureProperty) ? _textureProperty : "_MainTex";
            if (material.GetTexture(property) != _view.Texture)
                material.SetTexture(property, _view.Texture);
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

        /// <summary>The pointer position on the surface in page pixels (from the top left), or false when the ray misses the collider.</summary>
        private bool TryGetPagePosition(PointerEventData eventData, out Vector2 position)
        {
            position = default;
            Camera camera = eventData.pressEventCamera != null ? eventData.pressEventCamera : eventData.enterEventCamera;
            if (camera == null || !_collider.Raycast(camera.ScreenPointToRay(eventData.position), out RaycastHit hit, float.MaxValue))
                return false;

            position = new Vector2(hit.textureCoord.x * _view.Screen.Width, (1f - hit.textureCoord.y) * _view.Screen.Height);
            return true;
        }

        private static int ButtonOf(PointerEventData eventData)
        {
            return eventData.button == PointerEventData.InputButton.Right ? 1 : eventData.button == PointerEventData.InputButton.Middle ? 2 : 0;
        }
    }
}
