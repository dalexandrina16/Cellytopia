using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class VirusBehaviour : MonoBehaviour
{
    Boolean gameStarted = true;
    void Start() {
        InvokeRepeating("CustomUpdate", 1.0f, 1.0f);
    }

    void CustomUpdate()
    {
        if(gameStarted) {
            this.transform.position = new Vector3(
            this.transform.position.x + UnityEngine.Random.Range(-1, 2), 
            this.transform.position.y + UnityEngine.Random.Range(-1, 2)
            );
            if(this.transform.position.x > 1 && this.transform.position.x < 5 && this.transform.position.y > 1 && this.transform.position.y<3 && UnityEngine.Random.Range(0,3) == 0) {
                Instantiate(this, this.transform.position, Quaternion.identity);
                if(UnityEngine.Random.Range(0,2) == 1) {
                    Destroy(this.gameObject);
                }
            }
            if(UnityEngine.Random.Range(0,20) == 0) {
                Destroy(this.gameObject);
            }
        }
    }
}
