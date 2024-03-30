using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public class TransitionPassthroughScene : MonoBehaviour
{
    public InputField ipInputField;
    public InputField portInputField;

    public Button connectButton;

    public string sceneName;

    private void Start()
    {
        string ip = PlayerPrefs.GetString("IP");
        string port = PlayerPrefs.GetString("Port");

        ipInputField.text = ip;
        portInputField.text = port;

        connectButton.onClick.AddListener(() => TransitionToScene(sceneName));
    }

    public void TransitionToScene(string sceneName)
    {
        string ip = ipInputField.text;
        string port = portInputField.text;

        PlayerPrefs.SetString("IP", ip);
        PlayerPrefs.SetString("Port", port);

        SceneManager.LoadScene(sceneName);
    }
}
