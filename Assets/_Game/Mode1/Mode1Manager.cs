using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class Mode1Manager : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI _scoreText;
    [SerializeField] private Button btn_1;

    int _score = 0;

    /// <summary>
    /// Start is called before the first frame update
    /// </summary>
    void Start()
    {
        btn_1.onClick.AddListener(OnClickBtn1); 
    }

    private void OnClickBtn1()
    {
        _score++;
        _scoreText.text = "Score: " + _score;
    }
}
