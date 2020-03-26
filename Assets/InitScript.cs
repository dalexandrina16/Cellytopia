using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class InitScript : MonoBehaviour {
    public GameObject cell;
    GameObject [ , ] cells;

    void Start() {
        Instantiate(cell, Vector3.zero, Quaternion.identity);
    }

    void Update() {
        if(Input.GetMouseButton(0)) {
            Vector3 clicked = Camera.main.ScreenToWorldPoint(Input.mousePosition);
            clicked.z = 0;
            Instantiate(cell, clicked, Quaternion.identity);
        }
    }
}
