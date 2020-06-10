using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class VirusBehaviour : MonoBehaviour
{
    public Boolean gameStarted;
    void Start() {
        gameStarted = false;
        InvokeRepeating("CustomUpdate", 1.0f, 1.0f);
    }

    void CustomUpdate()
    {
        if(gameStarted) {
            this.transform.position = new Vector3(
            this.transform.position.x + UnityEngine.Random.Range(-1, 2), 
            this.transform.position.y + UnityEngine.Random.Range(-1, 2)
            );
            if(this.transform.position.x < 1 && this.transform.position.x > 5 && this.transform.position.y < 1 && this.transform.position.y > 3) {
                Instantiate(this, this.transform.position, Quaternion.identity);
                if(this.transform.position.x < -2 && this.transform.position.x > 7) {
                    Destroy(this.gameObject);
                }
            } else if(this.transform.position.x > 1 && this.transform.position.x < 5 && this.transform.position.y > 1 && this.transform.position.y < 3) {
                this.transform.localScale = new Vector3(1.5f, 1.5f, 1.5f);
            }
        }
    }
}
