using SentienceLab.OSC;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

public class OSC_IODevice : MonoBehaviour, IOSCVariableContainer
{
	[System.Serializable]
	public struct InputToEventMap
	{
		public string           OSC_Name;
		public UnityEvent<bool> OnInputActive;
	}

	public string Prefix = "/tracked_device";

	public List<InputToEventMap> Inputs;


	public void Start()
	{
		m_inputs = new List<OSC_BoolVariable>();

		foreach (var i in Inputs)
		{
			OSC_BoolVariable oscVar = new OSC_BoolVariable(Prefix + "/" + i.OSC_Name);
			oscVar.OnDataReceived += var => { OnUpdate(var, i.OnInputActive); };
			m_inputs.Add(oscVar);
		}
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
		return new List<OSC_Variable>(m_inputs);
	}


	protected List<OSC_BoolVariable> m_inputs;
}
