using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;
using UnityEngine.Networking;
using OVRSimpleJSON;
using System.Collections.Generic; // Add this namespace

public class TransitionPassthroughScene : MonoBehaviour
{
    public InputField projectIDInputField;
    public InputField passwordInputField;
    public Button loadButton;
    public Button connectButton;
    public TMP_Dropdown robotDropdown;
    public InputField turnServerURLInputField;
    public InputField turnServerUsernameInputField;
    public InputField turnServerPasswordInputField;
    public InputField videoRenderFrequencyInputField;
    public InputField dataSendFrequencyInputField;
    public InputField videoPlaneDistanceInputField;
    public InputField videoVFOVInputField;
    public TMP_Text debugText;
    public string sceneName;

    private void Start()
    {
        projectIDInputField.text = PlayerPrefs.GetString("ProjectID");
        passwordInputField.text = PlayerPrefs.GetString("Password");
        turnServerURLInputField.text = PlayerPrefs.GetString("TurnServerURL");
        turnServerUsernameInputField.text = PlayerPrefs.GetString("TurnServerUsername");
        turnServerPasswordInputField.text = PlayerPrefs.GetString("TurnServerPassword");
        if (PlayerPrefs.HasKey("VideoRenderFrequency"))
        {
            videoRenderFrequencyInputField.text = PlayerPrefs.GetFloat("VideoRenderFrequency").ToString();
        }
        else
        {
            videoRenderFrequencyInputField.text = "30";
        }
        if (PlayerPrefs.HasKey("DataSendFrequency"))
        {
            dataSendFrequencyInputField.text = PlayerPrefs.GetFloat("DataSendFrequency").ToString();
        }
        else
        {
            dataSendFrequencyInputField.text = "20";
        }
        if (PlayerPrefs.HasKey("VideoPlaneDistance"))
        {
            videoPlaneDistanceInputField.text = PlayerPrefs.GetFloat("VideoPlaneDistance").ToString();
        }
        else
        {
            videoPlaneDistanceInputField.text = "1.0";
        }
        if (PlayerPrefs.HasKey("VideoVFOV"))
        {
            videoVFOVInputField.text = PlayerPrefs.GetFloat("VideoVFOV").ToString();
        }
        else
        {
            videoVFOVInputField.text = "105";
        }

        loadButton.onClick.AddListener(() => loadRobots());
        connectButton.onClick.AddListener(() => TransitionToScene(sceneName));
    }

    public void loadRobots()
    {
        // Get projectID and password from input fields
        string projectID = projectIDInputField.text;
        string password = passwordInputField.text;

        // Save the projectID and password to PlayerPrefs
        PlayerPrefs.SetString("ProjectID", projectID);
        PlayerPrefs.SetString("Password", password);

        // Construct the Firestore REST API GET request URL
        string url = $"https://firestore.googleapis.com/v1/projects/{projectID}/databases/(default)/documents/{password}";

        // Send the HTTP GET request
        UnityWebRequest www = UnityWebRequest.Get(url);
        www.SendWebRequest();

        // Wait for the request to complete
        while (!www.isDone) {}

        // Check for errors
        if (www.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError(www.error);
            debugText.text = www.error;
            return;
        }
        // Parse the JSON response
        JSONNode json = JSON.Parse(www.downloadHandler.text);

        // Fill the dropdown with the list of robots
        robotDropdown.ClearOptions();
        foreach (JSONNode robot in json["documents"])
        {
            string documentName = robot["name"];
            string documentId = documentName.Substring(documentName.LastIndexOf("/") + 1);
            robotDropdown.AddOptions(new List<TMP_Dropdown.OptionData>() { new TMP_Dropdown.OptionData(documentId) });
        }
    }

    public void TransitionToScene(string sceneName)
    {
        PlayerPrefs.SetString("RobotID", robotDropdown.options[robotDropdown.value].text);
        PlayerPrefs.SetString("TurnServerURL", turnServerURLInputField.text);
        PlayerPrefs.SetString("TurnServerUsername", turnServerUsernameInputField.text);
        PlayerPrefs.SetString("TurnServerPassword", turnServerPasswordInputField.text);
        PlayerPrefs.SetFloat("VideoRenderFrequency", float.Parse(videoRenderFrequencyInputField.text));
        PlayerPrefs.SetFloat("DataSendFrequency", float.Parse(dataSendFrequencyInputField.text));
        SceneManager.LoadScene(sceneName);
    }
}