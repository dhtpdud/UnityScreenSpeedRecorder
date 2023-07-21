using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class Rotate : MonoBehaviour
{
    [SerializeField]
    private float speed = 1;
    private void FixedUpdate()
    {
        transform.Rotate(speed * Vector3.up * Time.deltaTime);
    }
}
