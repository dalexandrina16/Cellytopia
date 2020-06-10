using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class InitScript : MonoBehaviour {
    public GameObject cell;
    public GameObject virus;
    public GameObject covid;
    public GameObject button;

    public int selected = 0;

    void Update() {
        if(Input.GetMouseButton(0)) {
            Vector3 clicked = Camera.main.ScreenToWorldPoint(Input.mousePosition);
            Vector3 buttonPoss = new Vector3(5.809998f, 5.809998f, 0);
            float radius = 0.5f;
            float radiusSel = 0.2f;
            if (clicked.x < 5.809998f + radius && clicked.x > 5.809998f - radius && clicked.y < -3.376404f + radius && clicked.y > -3.376404f - radius) {
                ((CellBehaviour)cell.GetComponent<CellBehaviour>()).gameStarted = true;
                var cells = GameObject.FindGameObjectsWithTag("bacteria");
                var covids = GameObject.FindGameObjectsWithTag("covid");
                var viruses = GameObject.FindGameObjectsWithTag("virus");
                foreach(GameObject cell in cells) {
                    ((CellBehaviour)cell.GetComponent<CellBehaviour>()).gameStarted = true;
                }
                foreach(GameObject covid in covids) {
                    ((CovidBehavior)covid.GetComponent<CovidBehavior>()).gameStarted = true;
                }
                foreach(GameObject virus in viruses) {
                    ((VirusBehaviour)virus.GetComponent<VirusBehaviour>()).gameStarted = true;
                }
            } else if(clicked.x < 5.078f + radiusSel && clicked.x > 5.078f - radiusSel && clicked.y < -4.399f + radiusSel && clicked.y > -4.399f - radiusSel) {
                selected = 0;
            } else if(clicked.x < 5.839f + radiusSel && clicked.x > 5.839f - radiusSel && clicked.y < -4.4f + radiusSel && clicked.y > -4.4f - radiusSel) {
                selected = 1;
            } else if(clicked.x < 6.66f + radiusSel && clicked.x > 6.66f - radiusSel && clicked.y < -4.43f + radiusSel && clicked.y > -4.43f - radiusSel) {
                selected = 2;
            } else {
                clicked.z = 0;
                if(selected == 0) {
                    Instantiate(cell, clicked, Quaternion.identity);
                } else if(selected == 1) {
                        Instantiate(covid, clicked, Quaternion.identity);
                } else {
                        Instantiate(virus, clicked, Quaternion.identity);
                }
            }
        }
    }
}
