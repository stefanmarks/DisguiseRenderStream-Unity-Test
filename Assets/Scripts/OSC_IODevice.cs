using SentienceLab.OSC;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

public class OSC_IODevice : MonoBehaviour, IOSCVariableContainer
{
	public string Prefix = "/tracked_device";

	public UnityEvent<bool> OnInput1Event;
	public UnityEvent<bool> OnInput2Event;
	public UnityEvent<bool> OnInput3Event;
	public UnityEvent<bool> OnInput4Event;


	public void Start()
	{
		m_io1 = new OSC_BoolVariable(Prefix + "/input1");
		m_io2 = new OSC_BoolVariable(Prefix + "/input2");
		m_io3 = new OSC_BoolVariable(Prefix + "/input3");
		m_io4 = new OSC_BoolVariable(Prefix + "/input4");

		m_io1.OnDataReceived += var => { OnUpdate(var, OnInput1Event); };
		m_io2.OnDataReceived += var => { OnUpdate(var, OnInput2Event); };
		m_io3.OnDataReceived += var => { OnUpdate(var, OnInput3Event); };
		m_io4.OnDataReceived += var => { OnUpdate(var, OnInput4Event); };
	}


	protected void OnUpdate(OSC_Variable _var, UnityEvent<bool> _event)
	{
		_event.Invoke(((OSC_BoolVariable) _var).Value);
	}


	public void Update()
	{
		// nothing to do here
	}


	public List<OSC_Variable> GetOSC_Variables()
	{
		return new List<OSC_Variable>() { m_io1, m_io2, m_io3, m_io4 };
	}


	protected OSC_BoolVariable m_io1, m_io2, m_io3, m_io4;
}
