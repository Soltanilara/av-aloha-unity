using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;// Required when using Event data.

public class InputFieldKeyboardSelect : MonoBehaviour, ISelectHandler
{
    public OVRVirtualKeyboardInputFieldTextHandler keyboardInputFieldTextHandler;
    private InputField inputField;

    // Start is called before the first frame update
    void Start()
    {
        inputField = GetComponent<InputField>();
    }

    public void OnSelect (BaseEventData eventData) 
	{
        keyboardInputFieldTextHandler.InputField = inputField;
	}
}
