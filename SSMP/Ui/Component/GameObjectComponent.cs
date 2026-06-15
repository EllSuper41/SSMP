using UnityEngine;

namespace SSMP.Ui.Component;

/// <summary>
/// A thin <see cref="IComponent"/> adapter that wraps an externally-created raw <see cref="GameObject"/> so its
/// active state can be governed by a <see cref="ComponentGroup"/> tree, exactly like the other panels (e.g.
/// <see cref="LobbyConfigPanel"/>) do.
///
/// This is intentionally NOT a subclass of <see cref="Component"/>: <see cref="ComponentGroup.ReparentComponents"/>
/// only reparents <see cref="Component"/>-derived objects, so wrapping a GameObject here does not cause the group to
/// try to reparent it. The wrapped GameObject keeps the Unity parent it was created with; this adapter only decides
/// who toggles its activeSelf, unifying the previously-separate visibility authority.
/// </summary>
internal class GameObjectComponent : IComponent {
    /// <summary>The wrapped GameObject.</summary>
    private readonly GameObject _gameObject;

    /// <summary>Tracks this component's own active state.</summary>
    private bool _activeSelf;

    /// <summary>Parent component group for visibility management.</summary>
    private readonly ComponentGroup _componentGroup;

    /// <summary>
    /// Wraps an existing GameObject and registers it with the given component group. Does NOT re-parent the
    /// GameObject; it is assumed to already be correctly parented.
    /// </summary>
    /// <param name="parent">Parent component group.</param>
    /// <param name="gameObject">The externally-created GameObject to govern.</param>
    public GameObjectComponent(ComponentGroup parent, GameObject gameObject) {
        _gameObject = gameObject;
        _componentGroup = parent;
        _activeSelf = true;
        parent.AddComponent(this);
    }

    /// <inheritdoc />
    public void SetGroupActive(bool groupActive) {
        if (_gameObject == null) return;
        _gameObject.SetActive(_activeSelf && groupActive);
    }

    /// <inheritdoc />
    public void SetActive(bool active) {
        _activeSelf = active;
        if (_gameObject == null) return;
        _gameObject.SetActive(_activeSelf && _componentGroup.IsActive());
    }

    /// <inheritdoc />
    public Vector2 GetPosition() {
        var rectTransform = _gameObject.GetComponent<RectTransform>();
        var position = rectTransform.anchorMin;
        return new Vector2(position.x * 1920f, position.y * 1080f);
    }

    /// <inheritdoc />
    public void SetPosition(Vector2 position) {
        var rectTransform = _gameObject.GetComponent<RectTransform>();
        rectTransform.anchorMin = rectTransform.anchorMax = new Vector2(position.x / 1920f, position.y / 1080f);
    }

    /// <inheritdoc />
    public Vector2 GetSize() => _gameObject.GetComponent<RectTransform>().sizeDelta;
}
