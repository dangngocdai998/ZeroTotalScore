using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MenuManager : MonoBehaviour
{
    [SerializeField] private CCDManager ccdManager;

    //CCD
    [SerializeField] private string projectID = "2e7462df-5b31-4328-be0c-b19e5a9d523c";
    [SerializeField] private string environmentID = "1905ad40-4080-41de-afd8-600becbae739";
    [SerializeField] private string bucketID = "82435521-685b-446e-9c40-e390b57c7f88";

    // Start is called before the first frame update
    void Start()
    {
        if (ccdManager == null)
            ccdManager = CCDManager.Instance;

        if (ccdManager != null)
            ccdManager.Configure(projectID, bucketID, environmentID, string.Empty);
    }

    public void OpenMode1()
    {
        if (ccdManager == null)
        {
            Debug.LogError("CCDManager is missing from the scene.");
            return;
        }

        ccdManager.DownloadMode1IOS(
            () => Debug.Log("Mode1_iOS downloaded and opened."),
            error => Debug.LogError("Mode1_iOS failed: " + error));
    }
}
