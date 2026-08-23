using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;// Required when using Event data.

/// <summary>
/// Hands the selected InputField to the Meta virtual keyboard so the user can type
/// the connection details in the StartScene.
///
/// DISABLED as of Meta XR Core SDK 201.0.0: OVRVirtualKeyboardInputFieldTextHandler
/// (and the whole OVRVirtualKeyboard family) was removed from the package, and Meta
/// ships no replacement package in the registry. The class is kept — rather than
/// deleted — so the StartScene's component reference stays valid instead of turning
/// into a missing script.
///
/// TODO: restore text entry. Options are Unity's TouchScreenKeyboard, a hand-built
/// in-scene key grid, or pinning com.meta.xr.sdk.core back to 76.0.1.
/// </summary>
public class InputFieldKeyboardSelect : MonoBehaviour, ISelectHandler
{
    private InputField inputField;

    // Start is called before the first frame update
    void Start()
    {
        inputField = GetComponent<InputField>();
    }

    public void OnSelect(BaseEventData eventData)
    {
        // No-op: see the class summary. Selecting the field no longer raises a keyboard.
    }
}
