using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Styly.NetSync;

/// <summary>
/// Manages touch count TextMesh displays for all avatars in the scene.
/// Attach this to a GameObject in the scene to automatically add touch count
/// displays to remote avatars when they spawn.
/// </summary>
public class TouchCountDisplayManager : MonoBehaviour
{
    [Header("TextMesh Settings")]
    [SerializeField] private Font _font;
    [SerializeField] private int _fontSize = 64;
    [SerializeField] private float _characterSize = 0.02f;
    [SerializeField] private Color _textColor = Color.white;
    [SerializeField] private TextAnchor _anchor = TextAnchor.MiddleCenter;
    [SerializeField] private TextAlignment _alignment = TextAlignment.Center;

    [Header("Position Settings")]
    [SerializeField] private Vector3 _headOffset = new Vector3(0, 0.25f, 0);

    // Track which avatars already have displays
    private HashSet<int> _avatarsWithDisplays = new HashSet<int>();

    void OnEnable()
    {
        if (NetSyncManager.Instance != null)
        {
            NetSyncManager.Instance.OnAvatarConnected.AddListener(OnAvatarConnected);
        }
    }

    void OnDisable()
    {
        if (NetSyncManager.Instance != null)
        {
            NetSyncManager.Instance.OnAvatarConnected.RemoveListener(OnAvatarConnected);
        }
    }

    void OnAvatarConnected(int clientNo)
    {
        // Start coroutine to find and setup the avatar (need to wait a frame for spawning)
        StartCoroutine(SetupAvatarDisplayDelayed(clientNo));
    }

    IEnumerator SetupAvatarDisplayDelayed(int clientNo)
    {
        // Wait for the avatar to be fully spawned
        yield return null;

        // Skip if already processed
        if (_avatarsWithDisplays.Contains(clientNo))
        {
            yield break;
        }

        // Find the remote avatar with this client number
        NetSyncAvatar[] avatars = FindObjectsByType<NetSyncAvatar>(FindObjectsSortMode.None);
        foreach (var avatar in avatars)
        {
            if (avatar != null && avatar.ClientNo == clientNo && !avatar.IsLocalAvatar)
            {
                CreateAvatarTouchCountDisplay(avatar);
                _avatarsWithDisplays.Add(clientNo);
                break;
            }
        }
    }

    void CreateAvatarTouchCountDisplay(NetSyncAvatar avatar)
    {
        // Create a new GameObject for the TextMesh
        GameObject textObj = new GameObject("TouchCountDisplay");

        // Add TextMesh component
        TextMesh textMesh = textObj.AddComponent<TextMesh>();
        textMesh.fontSize = _fontSize;
        textMesh.characterSize = _characterSize;
        textMesh.color = _textColor;
        textMesh.anchor = _anchor;
        textMesh.alignment = _alignment;

        if (_font != null)
        {
            textMesh.font = _font;
            // Set the font material for proper rendering
            MeshRenderer renderer = textObj.GetComponent<MeshRenderer>();
            if (renderer != null && _font.material != null)
            {
                renderer.material = _font.material;
            }
        }

        // Parent to the avatar (not to head directly to allow offset positioning)
        textObj.transform.SetParent(avatar.transform, false);

        // Add the display script with offset setting
        AvatarTouchCountDisplay display = textObj.AddComponent<AvatarTouchCountDisplay>();
    }
}
