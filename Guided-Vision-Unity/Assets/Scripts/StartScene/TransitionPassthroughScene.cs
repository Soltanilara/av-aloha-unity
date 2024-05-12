using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using Firebase.Firestore;
using TMPro;
using System.Linq;
using Firebase.Extensions;

public class TransitionPassthroughScene : MonoBehaviour
{
    public TMP_Dropdown dropdown;
    public Button connectButton;
    public TextMeshProUGUI debugText;
    public string sceneName;
    private FirebaseFirestore firestore = null;
    private ListenerRegistration listenerRegistration = null;

    private void Start()
    {
        // create firestore instance
        firestore = FirebaseFirestore.DefaultInstance;

        listenerRegistration = firestore.Collection("calls").Listen(snapshot =>
        {
            dropdown.ClearOptions();
            dropdown.value = 0;

            if (snapshot.Documents.Count() == 0)
            {
                debugText.text = "No robots available to connect to.";
            }
            else {
                foreach (DocumentSnapshot documentSnapshot in snapshot.Documents)
                {
                    if (documentSnapshot.Exists)
                    {
                        dropdown.options.Add(new TMP_Dropdown.OptionData(documentSnapshot.Id));
                        Debug.Log("Document data for " + documentSnapshot.Id + " document: " + documentSnapshot.ToDictionary());
                    }

                    // set dropdown value to first option
                    dropdown.value = 1;
                }
            }
        });



        connectButton.onClick.AddListener(() => TransitionToScene(sceneName));
    }

    public void TransitionToScene(string sceneName)
    {
        // check if dropdown has a valid value
        if (dropdown.options[dropdown.value].text == "")
        {
            debugText.text = "Please select a robot to connect to.";
            return;
        }

        PlayerPrefs.SetString("RobotID", dropdown.options[dropdown.value].text);
        SceneManager.LoadScene(sceneName);
    }

    private void OnDestroy()
    {
        if (listenerRegistration != null)
        {
            listenerRegistration.Stop();
        }
    }
}
