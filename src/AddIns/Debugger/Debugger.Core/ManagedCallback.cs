﻿// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Debugger.Interop;
using Debugger.Interop.CorDebug;

namespace Debugger
{
	/// <summary>
	/// Handles all callbacks of a given process
	/// </summary>
	/// <remarks>
	/// Note that there can be a queued callback after almost any callback.
	/// In particular:
	///  - After 'break' there may be more callbacks
	///  - After EvalComplete there may be more callbacks (eg CreateThread from other thread)
	/// </remarks>
	class ManagedCallback
	{
		Process process;
		bool pauseOnNextExit;
		bool isInCallback;
		DebuggerPausedEventArgs pausedEventArgs;
		
		Thread threadToReport; // TODO: Remove
		
		[Debugger.Tests.Ignore]
		public Process Process {
			get { return process; }
		}
		
		public bool IsInCallback {
			get { return isInCallback; }
		}
		
		public ManagedCallback(Process process)
		{
			this.process = process;
		}
		
		// Calling this will prepare the paused event.
		// The reason for the accumulation is that several pause callbacks
		// can happen "at the same time" in the debugee.
		// The event will be raised as soon as the callback queue is drained.
		DebuggerPausedEventArgs GetPausedEventArgs()
		{
			if (pausedEventArgs == null) {
				pausedEventArgs = new DebuggerPausedEventArgs();
				pausedEventArgs.Process = process;
				pausedEventArgs.BreakpointsHit = new List<Breakpoint>();
			}
			return pausedEventArgs;
		}
		
		void EnterCallback(string name, ICorDebugProcess pProcess)
		{
			isInCallback = true;
			
			process.TraceMessage("Callback: " + name);
			System.Diagnostics.Debug.Assert(process.CorProcess == pProcess);
			
			// After break is pressed we may receive some messages that were already queued
			if (process.IsPaused) {
				process.TraceMessage("Processing post-break callback");
				// Decrese the "break count" from 2 to 1 - does not actually continue
				process.AsyncContinue(DebuggeeStateAction.Keep, new Thread[] {}, null);
				// Make sure we stay pauses after the callback is handled
				pauseOnNextExit = true;
				return;
			}
			
			if (process.IsRunning) {
				process.NotifyPaused();
				return;
			}
			
			throw new DebuggerException("Invalid state at the start of callback");
		}
		
		void EnterCallback(string name, ICorDebugAppDomain pAppDomain)
		{
			EnterCallback(name, pAppDomain.GetProcess());
		}
		
		void EnterCallback(string name, ICorDebugThread pThread)
		{
			EnterCallback(name, pThread.GetProcess());
			threadToReport = process.GetThread(pThread);
		}
		
		void ExitCallback()
		{
			bool hasQueuedCallbacks = process.CorProcess.HasQueuedCallbacks();
			if (hasQueuedCallbacks)
				process.TraceMessage("Process has queued callbacks");
			
			// TODO: Can we drain the queue?
			if (hasQueuedCallbacks && (pausedEventArgs == null || pausedEventArgs.ExceptionThrown == null)) {
				// Process queued callbacks if no exception occurred
				process.AsyncContinue(DebuggeeStateAction.Keep, null, null);
			} else if (process.Evaluating) {
				// Ignore events during property evaluation
				process.AsyncContinue(DebuggeeStateAction.Keep, null, null);
			} else if (pauseOnNextExit) {
				if (process.Options.Verbose)
					process.TraceMessage("Callback exit: Paused");

				process.DisableAllSteppers();
				if (pausedEventArgs != null) {
					pausedEventArgs.Thread = threadToReport;
					threadToReport = null;
					
					// Raise the pause event outside the callback
					// Warning: Make sure that process in not resumed in the meantime
					DebuggerPausedEventArgs e = pausedEventArgs; // Copy for capture
					process.Debugger.MTA2STA.AsyncCall(delegate { process.OnPaused(e); });
				}
				
				pauseOnNextExit = false;
				pausedEventArgs = null;
			} else {
				process.AsyncContinue(DebuggeeStateAction.Keep, null, null);
			}
			
			isInCallback = false;
		}
		
		#region Program folow control
		
		public void StepComplete(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugStepper pStepper, CorDebugStepReason reason)
		{
			EnterCallback("StepComplete (" + reason.ToString() + ")", pThread);
			
			Thread thread = process.GetThread(pThread);
			Stepper stepper = process.GetStepper(pStepper);
			
			StackFrame currentStackFrame = thread.MostRecentStackFrame;
			process.TraceMessage(" - stopped at {0} because of {1}", currentStackFrame.MethodInfo.FullName, stepper.ToString());
			
			process.Steppers.Remove(stepper);
			stepper.OnStepComplete(reason);
			
			if (stepper.Ignore) {
				// The stepper is ignored
				process.TraceMessage(" - ignored");
			} else if (thread.CurrentStepIn != null &&
			           thread.CurrentStepIn.StackFrame.Equals(currentStackFrame) &&
			           thread.CurrentStepIn.IsInStepRanges((int)currentStackFrame.IP)) {
				Stepper.StepIn(currentStackFrame, thread.CurrentStepIn.StepRanges, "finishing step in");
				process.TraceMessage(" - finishing step in");
			} else if (currentStackFrame.IsNonUserCode) {
				if (process.Options.EnableJustMyCode) {
					currentStackFrame.MarkAsNonUserCode();
					process.TraceMessage(" - method {0} marked as non user code", currentStackFrame.MethodInfo.FullName);
					Stepper.StepIn(currentStackFrame, new int[] {0, int.MaxValue}, "seeking user code");
					process.TraceMessage(" - seeking user code");
				} else {
					Stepper.StepOut(currentStackFrame, "stepping out of non-user code");
					process.TraceMessage(" - stepping out of non-user code");
				}
			} else {
				// User-code method
				pauseOnNextExit = true;
				GetPausedEventArgs().Break = true;
				process.TraceMessage(" - pausing in user code");
			}
			
			ExitCallback();
		}
		
		// Warning! Marshaing of ICorBreakpoint fails in .NET 1.1
		public void Breakpoint(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugBreakpoint corBreakpoint)
		{
			EnterCallback("Breakpoint", pThread);
			
			Breakpoint breakpoint = process.Debugger.GetBreakpoint(corBreakpoint);
			// Could be one of Process.tempBreakpoints
			// The breakpoint might have just been removed
			if (breakpoint != null) {
				GetPausedEventArgs().BreakpointsHit.Add(breakpoint);
			} else {
				GetPausedEventArgs().Break = true;
			}
			
			pauseOnNextExit = true;
			
			ExitCallback();
		}
		
		public void BreakpointSetError(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugBreakpoint pBreakpoint, uint dwError)
		{
			EnterCallback("BreakpointSetError", pThread);
			
			ExitCallback();
		}
		
		public void Break(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread)
		{
			EnterCallback("Break", pThread);

			pauseOnNextExit = true;
			GetPausedEventArgs().Break = true;
			ExitCallback();
		}

		public void ControlCTrap(ICorDebugProcess pProcess)
		{
			EnterCallback("ControlCTrap", pProcess);

			pauseOnNextExit = true;
			GetPausedEventArgs().Break = true;
			ExitCallback();
		}

		public void Exception(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, int unhandled)
		{
			// Exception2 is used in .NET Framework 2.0
			
			if (process.DebuggeeVersion.StartsWith("v1.")) {
				// Forward the call to Exception2, which handles EnterCallback and ExitCallback
				ExceptionType exceptionType = (unhandled != 0) ? ExceptionType.Unhandled : ExceptionType.FirstChance;
				Exception2(pAppDomain, pThread, null, 0, (CorDebugExceptionCallbackType)exceptionType, 0);
			} else {
				// This callback should be ignored in v2 applications
				EnterCallback("Exception", pThread);
				
				ExitCallback();
			}
		}

		#endregion

		#region Various

		public void LogSwitch(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, int lLevel, uint ulReason, string pLogSwitchName, string pParentName)
		{
			EnterCallback("LogSwitch", pThread);

			ExitCallback();
		}
		
		public void LogMessage(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, int lLevel, string pLogSwitchName, string pMessage)
		{
			EnterCallback("LogMessage", pThread);

			process.OnLogMessage(new MessageEventArgs(process, lLevel, pMessage, pLogSwitchName));

			ExitCallback();
		}

		public void EditAndContinueRemap(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugFunction pFunction, int fAccurate)
		{
			EnterCallback("EditAndContinueRemap", pThread);

			ExitCallback();
		}
		
		public void EvalException(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugEval corEval)
		{
			EnterCallback("EvalException (" + process.GetActiveEval(corEval).Description + ")", pThread);
			
			HandleEvalComplete(pAppDomain, pThread, corEval, true);
		}
		
		public void EvalComplete(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugEval corEval)
		{
			EnterCallback("EvalComplete (" + process.GetActiveEval(corEval).Description + ")", pThread);
			
			HandleEvalComplete(pAppDomain, pThread, corEval, false);
		}
		
		void HandleEvalComplete(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugEval corEval, bool exception)
		{
			// Let the eval know that the CorEval has finished
			Eval eval = process.GetActiveEval(corEval);
			eval.NotifyEvaluationComplete(!exception);
			process.activeEvals.Remove(eval);
			
			pauseOnNextExit = true;
			ExitCallback();
		}
		
		public void DebuggerError(ICorDebugProcess pProcess, int errorHR, uint errorCode)
		{
			EnterCallback("DebuggerError", pProcess);

			string errorText = String.Format("Debugger error: \nHR = 0x{0:X} \nCode = 0x{1:X}", errorHR, errorCode);
			
			if ((uint)errorHR == 0x80131C30) {
				errorText += "\n\nDebugging 64-bit processes is currently not supported.\n" +
					"If you are running a 64-bit system, this setting might help:\n" +
					"Project -> Project Options -> Compiling -> Target CPU = 32-bit Intel";
			}
			
			if (Environment.UserInteractive)
				System.Windows.Forms.MessageBox.Show(errorText);
			else
				throw new DebuggerException(errorText);

			try {
				pauseOnNextExit = true;
				GetPausedEventArgs().Break = true;
				ExitCallback();
			} catch (COMException) {
			} catch (InvalidComObjectException) {
				// ignore errors during shutdown after debugger error
			}
		}

		public void UpdateModuleSymbols(ICorDebugAppDomain pAppDomain, ICorDebugModule pModule, IStream pSymbolStream)
		{
			EnterCallback("UpdateModuleSymbols", pAppDomain);
			
			Module module = process.GetModule(pModule);
			if (module.CorModule is ICorDebugModule3 && module.IsDynamic) {
				// In .NET 4.0, we use the LoadClass callback to load dynamic modules
				// because it always works - UpdateModuleSymbols does not.
				//  - Simple dynamic code generation seems to trigger both callbacks.
				//  - IronPython for some reason causes just the LoadClass callback
				//    so we choose to rely on it out of the two.
			} else {
				// In .NET 2.0, this is the the only method and it works fine
				module.LoadSymbolsFromMemory(pSymbolStream);
			}
			
			ExitCallback();
		}

		#endregion

		#region Start of Application

		public void CreateProcess(ICorDebugProcess pProcess)
		{
			EnterCallback("CreateProcess", pProcess);

			// Process is added in NDebugger.Start
			// disable NGen
			if (!this.process.Options.EnableJustMyCode && !this.process.Options.StepOverNoSymbols) {
				ICorDebugProcess2 pProcess2 = pProcess as ICorDebugProcess2;
				if (pProcess2 != null && Process.DebugMode == DebugModeFlag.Debug) {
					try {
						pProcess2.SetDesiredNGENCompilerFlags((uint)CorDebugJITCompilerFlags.CORDEBUG_JIT_DISABLE_OPTIMIZATION);
					} catch (COMException) {
						// we cannot set the NGEN flag => no evaluation for optimized code.
					}
				}
			}

			ExitCallback();
		}

		public void CreateAppDomain(ICorDebugProcess pProcess, ICorDebugAppDomain pAppDomain)
		{
			EnterCallback("CreateAppDomain", pAppDomain);

			pAppDomain.Attach();
			process.appDomains.Add(new AppDomain(process, pAppDomain));

			ExitCallback();
		}

		public void LoadAssembly(ICorDebugAppDomain pAppDomain, ICorDebugAssembly pAssembly)
		{
			EnterCallback("LoadAssembly", pAppDomain);

			ExitCallback();
		}

		public void LoadModule(ICorDebugAppDomain pAppDomain, ICorDebugModule pModule)
		{
			EnterCallback("LoadModule " + pModule.GetName(), pAppDomain);
			
			Module module = new Module(process.GetAppDomain(pAppDomain), pModule);
			process.modules.Add(module);
			process.OnModuleLoaded(module);
			
			ExitCallback();
		}
		
		public void NameChange(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread)
		{
			if (pAppDomain != null) {
				
				EnterCallback("NameChange: pAppDomain", pAppDomain);
				
				ExitCallback();
				
			}
			if (pThread != null) {
				
				EnterCallback("NameChange: pThread", pThread);
				
				ExitCallback();
				
			}
		}
		
		public void CreateThread(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread)
		{
			// We can not use pThread since it has not been added yet
			// and we continue from this callback anyway
			EnterCallback("CreateThread " + pThread.GetID(), pAppDomain);
			
			Thread thread = new Thread(process, pThread);
			process.threads.Add(thread);
			
			thread.CorThread.SetDebugState(process.NewThreadState);
			
			ExitCallback();
		}
		
		public void LoadClass(ICorDebugAppDomain pAppDomain, ICorDebugClass c)
		{
			EnterCallback("LoadClass", pAppDomain);
			
			Module module = process.GetModule(c.GetModule());
			
			// Dynamic module has been extended - reload symbols to inlude new class
			module.LoadSymbolsDynamic();
			
			ExitCallback();
		}
		
		#endregion
		
		#region Exit of Application
		
		public void UnloadClass(ICorDebugAppDomain pAppDomain, ICorDebugClass c)
		{
			EnterCallback("UnloadClass", pAppDomain);
			
			ExitCallback();
		}
		
		public void UnloadModule(ICorDebugAppDomain pAppDomain, ICorDebugModule pModule)
		{
			EnterCallback("UnloadModule", pAppDomain);
			
			Module module = process.GetModule(pModule);
			process.modules.Remove(module);
			process.OnModuleUnloaded(module);
			
			ExitCallback();
		}
		
		public void UnloadAssembly(ICorDebugAppDomain pAppDomain, ICorDebugAssembly pAssembly)
		{
			EnterCallback("UnloadAssembly", pAppDomain);
			
			ExitCallback();
		}
		
		public void ExitThread(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread)
		{
			Thread thread = process.GetThread(pThread);
			
			// ICorDebugThread is still not dead and can be used for some operations
			if (thread != null) {
				EnterCallback("ExitThread " + pThread.GetID(), pThread);
				
				thread.NotifyExited();
			} else {
				EnterCallback("ExitThread " + pThread.GetID(), process.CorProcess);
				
				// .NET 4.0 - It seems that the API is reporting exits of threads without announcing their creation.
				// TODO: Remove in next .NET 4.0 beta and investigate
				process.TraceMessage("ERROR: Thread does not exist " + pThread.GetID());
			}
			
			try {
				ExitCallback();
			} catch (COMException e) {
				// For some reason this sometimes happens in .NET 1.1
				process.TraceMessage("Continue failed in ExitThread callback: " + e.Message);
			}
		}
		
		public void ExitAppDomain(ICorDebugProcess pProcess, ICorDebugAppDomain pAppDomain)
		{
			EnterCallback("ExitAppDomain", pAppDomain);
			
			process.appDomains.Remove(process.GetAppDomain(pAppDomain));
			
			ExitCallback();
		}
		
		public void ExitProcess(ICorDebugProcess pProcess)
		{
			// ExitProcess may be called at any time when debuggee is killed
			process.TraceMessage("Callback: ExitProcess");
			
			process.OnExited();
		}
		
		#endregion
		
		#region ICorDebugManagedCallback2 Members
		
		public void ChangeConnection(ICorDebugProcess pProcess, uint dwConnectionId)
		{
			EnterCallback("ChangeConnection", pProcess);
			
			ExitCallback();
		}

		public void CreateConnection(ICorDebugProcess pProcess, uint dwConnectionId, IntPtr pConnName)
		{
			EnterCallback("CreateConnection", pProcess);
			
			ExitCallback();
		}

		public void DestroyConnection(ICorDebugProcess pProcess, uint dwConnectionId)
		{
			EnterCallback("DestroyConnection", pProcess);
			
			ExitCallback();
		}

		public void Exception2(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugFrame pFrame, uint nOffset, CorDebugExceptionCallbackType _exceptionType, uint dwFlags)
		{
			EnterCallback("Exception2 (type=" + _exceptionType.ToString() + ")", pThread);
			
			// This callback is also called from Exception(...)!!!! (the .NET 1.1 version)
			// Watch out for the zeros and null!
			// Exception -> Exception2(pAppDomain, pThread, null, 0, exceptionType, 0);
			
			ExceptionType exceptionType = (ExceptionType)_exceptionType;
			bool pauseOnHandled = !process.Evaluating && process.Options != null && process.Options.PauseOnHandledExceptions;
			
			if (exceptionType == ExceptionType.Unhandled || (pauseOnHandled && exceptionType == ExceptionType.CatchHandlerFound)) {
				Value value = new Value(process.GetAppDomain(pAppDomain), pThread.GetCurrentException()).GetPermanentReferenceOfHeapValue();
				
				if (GetPausedEventArgs().ExceptionThrown != null)
					throw new DebuggerException("Exception is already being processed");
				GetPausedEventArgs().ExceptionThrown = new Exception(value, exceptionType);
				
				pauseOnNextExit = true;
			}
			
			ExitCallback();
		}

		public void ExceptionUnwind(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, CorDebugExceptionUnwindCallbackType dwEventType, uint dwFlags)
		{
			EnterCallback("ExceptionUnwind", pThread);
			
			if (dwEventType == CorDebugExceptionUnwindCallbackType.DEBUG_EXCEPTION_INTERCEPTED) {
				pauseOnNextExit = true;
			}
			ExitCallback();
		}

		public void FunctionRemapComplete(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugFunction pFunction)
		{
			EnterCallback("FunctionRemapComplete", pThread);
			
			ExitCallback();
		}

		public void FunctionRemapOpportunity(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugFunction pOldFunction, ICorDebugFunction pNewFunction, uint oldILOffset)
		{
			EnterCallback("FunctionRemapOpportunity", pThread);
			
			ExitCallback();
		}

		/// <exception cref="Exception">Unknown callback argument</exception>
		public void MDANotification(ICorDebugController c, ICorDebugThread t, ICorDebugMDA mda)
		{
			if (c is ICorDebugAppDomain) {
				EnterCallback("MDANotification", (ICorDebugAppDomain)c);
			} else if (c is ICorDebugProcess){
				EnterCallback("MDANotification", (ICorDebugProcess)c);
			} else {
				throw new System.Exception("Unknown callback argument");
			}
			
			ExitCallback();
		}

		#endregion
	}
}
