using UnityEngine;
using UnityEngine.SceneManagement;

public class TransitionStartScene : MonoBehaviour
{
    public string sceneName;

    // Update is called once per frame
    void Update()
    {
        // if the user presses the A button, load the scene
        // or if keyboard space key is pressed
        if (OVRInput.GetDown(OVRInput.Button.Two) || Input.GetKeyDown(KeyCode.Space))
        {
            SceneManager.LoadScene(sceneName);
        }
    }
}
