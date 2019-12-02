// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.
//

//

#include "common.h"

#include "corhost.h"
#include "synch.h"

void CLREventBase::CreateAutoEvent (BOOL bInitialState  // If TRUE, initial state is signalled
                                )
{
    CONTRACTL
    {
        THROWS;
        GC_NOTRIGGER;
        // disallow creation of Crst before EE starts
        // Can not assert here. ASP.NET uses our Threadpool before EE is started.
        PRECONDITION((m_handle == INVALID_HANDLE_VALUE));
        PRECONDITION((!IsOSEvent()));
    }
    CONTRACTL_END;

    SetAutoEvent();

    {
        HANDLE h = WszCreateEvent(NULL,FALSE,bInitialState,NULL);
        if (h == NULL) {
            ThrowOutOfMemory();
        }
        m_handle = h;
    }

}

BOOL CLREventBase::CreateAutoEventNoThrow (BOOL bInitialState  // If TRUE, initial state is signalled
                                )
{
    CONTRACTL
    {
        NOTHROW;
        GC_NOTRIGGER;
        // disallow creation of Crst before EE starts
        // Can not assert here. ASP.NET uses our Threadpool before EE is started.
        PRECONDITION((m_handle == INVALID_HANDLE_VALUE));
        PRECONDITION((!IsOSEvent()));
    }
    CONTRACTL_END;

    EX_TRY
    {
        CreateAutoEvent(bInitialState);
    }
    EX_CATCH
    {
    }
    EX_END_CATCH(SwallowAllExceptions);

    return IsValid();
}

void CLREventBase::CreateManualEvent (BOOL bInitialState  // If TRUE, initial state is signalled
                                )
{
    CONTRACTL
    {
        THROWS;
        GC_NOTRIGGER;
        // disallow creation of Crst before EE starts
        // Can not assert here. ASP.NET uses our Threadpool before EE is started.
        PRECONDITION((m_handle == INVALID_HANDLE_VALUE));
        PRECONDITION((!IsOSEvent()));
    }
    CONTRACTL_END;

    {
        HANDLE h = WszCreateEvent(NULL,TRUE,bInitialState,NULL);
        if (h == NULL) {
            ThrowOutOfMemory();
        }
        m_handle = h;
    }
}

BOOL CLREventBase::CreateManualEventNoThrow (BOOL bInitialState  // If TRUE, initial state is signalled
                                )
{
    CONTRACTL
    {
        NOTHROW;
        GC_NOTRIGGER;
        // disallow creation of Crst before EE starts
        // Can not assert here. ASP.NET uses our Threadpool before EE is started.
        PRECONDITION((m_handle == INVALID_HANDLE_VALUE));
        PRECONDITION((!IsOSEvent()));
    }
    CONTRACTL_END;

    EX_TRY
    {
        CreateManualEvent(bInitialState);
    }
    EX_CATCH
    {
    }
    EX_END_CATCH(SwallowAllExceptions);

    return IsValid();
}

void CLREventBase::CreateMonitorEvent(SIZE_T Cookie)
{
    CONTRACTL
    {
        THROWS;
        GC_NOTRIGGER;
        // disallow creation of Crst before EE starts
        PRECONDITION((g_fEEStarted));
        PRECONDITION((GetThread() != NULL));
        PRECONDITION((!IsOSEvent()));
    }
    CONTRACTL_END;

    // thread-safe SetAutoEvent
    FastInterlockOr(&m_dwFlags, CLREVENT_FLAGS_AUTO_EVENT);

    {
        HANDLE h = WszCreateEvent(NULL,FALSE,FALSE,NULL);
        if (h == NULL) {
            ThrowOutOfMemory();
        }
        if (FastInterlockCompareExchangePointer(&m_handle,
                                                h,
                                                INVALID_HANDLE_VALUE) != INVALID_HANDLE_VALUE)
        {
            // We lost the race
            CloseHandle(h);
        }
    }

    // thread-safe SetInDeadlockDetection
    FastInterlockOr(&m_dwFlags, CLREVENT_FLAGS_IN_DEADLOCK_DETECTION);

    for (;;)
    {
        LONG oldFlags = m_dwFlags;

        if (oldFlags & CLREVENT_FLAGS_MONITOREVENT_ALLOCATED)
        {
            // Other thread has set the flag already. Nothing left for us to do.
            break;
        }

        LONG newFlags = oldFlags | CLREVENT_FLAGS_MONITOREVENT_ALLOCATED;
        if (FastInterlockCompareExchange((LONG*)&m_dwFlags, newFlags, oldFlags) != oldFlags)
        {
            // We lost the race
            continue;
        }

        // Because we set the allocated bit, we are the ones to do the signalling
        if (oldFlags & CLREVENT_FLAGS_MONITOREVENT_SIGNALLED)
        {
            // We got the honour to signal the event
            Set();
        }
        break;
    }
}


void CLREventBase::SetMonitorEvent()
{
    CONTRACTL
    {
        NOTHROW;
        GC_NOTRIGGER;
    }
    CONTRACTL_END;

    // SetMonitorEvent is robust against initialization races. It is possible to
    // call CLREvent::SetMonitorEvent on event that has not been initialialized yet by CreateMonitorEvent.
    // CreateMonitorEvent will signal the event once it is created if it happens.

    for (;;)
    {
        LONG oldFlags = m_dwFlags;

        if (oldFlags & CLREVENT_FLAGS_MONITOREVENT_ALLOCATED)
        {
            // Event has been allocated already. Use the regular codepath.
            Set();
            break;
        }

        LONG newFlags = oldFlags | CLREVENT_FLAGS_MONITOREVENT_SIGNALLED;
        if (FastInterlockCompareExchange((LONG*)&m_dwFlags, newFlags, oldFlags) != oldFlags)
        {
            // We lost the race
            continue;
        }
        break;
    }
}



void CLREventBase::CreateOSAutoEvent (BOOL bInitialState  // If TRUE, initial state is signalled
                                )
{
    CONTRACTL
    {
        THROWS;
        GC_NOTRIGGER;
        // disallow creation of Crst before EE starts
        PRECONDITION((m_handle == INVALID_HANDLE_VALUE));
    }
    CONTRACTL_END;

    // Can not assert here. ASP.NET uses our Threadpool before EE is started.
    //_ASSERTE (g_fEEStarted);

    SetOSEvent();
    SetAutoEvent();

    HANDLE h = WszCreateEvent(NULL,FALSE,bInitialState,NULL);
    if (h == NULL) {
        ThrowOutOfMemory();
    }
    m_handle = h;
}

BOOL CLREventBase::CreateOSAutoEventNoThrow (BOOL bInitialState  // If TRUE, initial state is signalled
                                )
{
    CONTRACTL
    {
        NOTHROW;
        GC_NOTRIGGER;
        // disallow creation of Crst before EE starts
        PRECONDITION((m_handle == INVALID_HANDLE_VALUE));
    }
    CONTRACTL_END;

    EX_TRY
    {
        CreateOSAutoEvent(bInitialState);
    }
    EX_CATCH
    {
    }
    EX_END_CATCH(SwallowAllExceptions);

    return IsValid();
}

void CLREventBase::CreateOSManualEvent (BOOL bInitialState  // If TRUE, initial state is signalled
                                )
{
    CONTRACTL
    {
        THROWS;
        GC_NOTRIGGER;
        // disallow creation of Crst before EE starts
        PRECONDITION((m_handle == INVALID_HANDLE_VALUE));
    }
    CONTRACTL_END;

    // Can not assert here. ASP.NET uses our Threadpool before EE is started.
    //_ASSERTE (g_fEEStarted);

    SetOSEvent();

    HANDLE h = WszCreateEvent(NULL,TRUE,bInitialState,NULL);
    if (h == NULL) {
        ThrowOutOfMemory();
    }
    m_handle = h;
}

BOOL CLREventBase::CreateOSManualEventNoThrow (BOOL bInitialState  // If TRUE, initial state is signalled
                                )
{
    CONTRACTL
    {
        NOTHROW;
        GC_NOTRIGGER;
        // disallow creation of Crst before EE starts
        PRECONDITION((m_handle == INVALID_HANDLE_VALUE));
    }
    CONTRACTL_END;

    EX_TRY
    {
        CreateOSManualEvent(bInitialState);
    }
    EX_CATCH
    {
    }
    EX_END_CATCH(SwallowAllExceptions);

    return IsValid();
}

void CLREventBase::CloseEvent()
{
    CONTRACTL
    {
      NOTHROW;
      if (IsInDeadlockDetection()) {GC_TRIGGERS;} else {GC_NOTRIGGER;}
    }
    CONTRACTL_END;

    GCX_MAYBE_PREEMP(IsInDeadlockDetection() && IsValid());

    _ASSERTE(Thread::Debug_AllowCallout());

    if (m_handle != INVALID_HANDLE_VALUE) {
        {
            CloseHandle(m_handle);
        }

        m_handle = INVALID_HANDLE_VALUE;
    }
    m_dwFlags = 0;
}


BOOL CLREventBase::Set()
{
    CONTRACTL
    {
      NOTHROW;
      GC_NOTRIGGER;
      PRECONDITION((m_handle != INVALID_HANDLE_VALUE));
    }
    CONTRACTL_END;

    _ASSERTE(Thread::Debug_AllowCallout());

    {
        return SetEvent(m_handle);
    }

}


BOOL CLREventBase::Reset()
{
    CONTRACTL
    {
      NOTHROW;
      GC_NOTRIGGER;
      PRECONDITION((m_handle != INVALID_HANDLE_VALUE));
    }
    CONTRACTL_END;

    _ASSERTE(Thread::Debug_AllowCallout());

    // We do not allow Reset on AutoEvent
    _ASSERTE (!IsAutoEvent() ||
              !"Can not call Reset on AutoEvent");

    {
        return ResetEvent(m_handle);
    }
}


static DWORD CLREventWaitHelper2(HANDLE handle, DWORD dwMilliseconds, BOOL alertable)
{
    STATIC_CONTRACT_THROWS;

    return WaitForSingleObjectEx(handle,dwMilliseconds,alertable);
}

static DWORD CLREventWaitHelper(HANDLE handle, DWORD dwMilliseconds, BOOL alertable)
{
    STATIC_CONTRACT_NOTHROW;

    struct Param
    {
        HANDLE handle;
        DWORD dwMilliseconds;
        BOOL alertable;
        DWORD result;
    } param;
    param.handle = handle;
    param.dwMilliseconds = dwMilliseconds;
    param.alertable = alertable;
    param.result = WAIT_FAILED;

    // Can not use EX_TRY/CATCH.  EX_CATCH toggles GC mode.  This function is called
    // through RareDisablePreemptiveGC.  EX_CATCH breaks profiler callback.
    PAL_TRY(Param *, pParam, &param)
    {
        // Need to move to another helper (cannot have SEH and C++ destructors
        // on automatic variables in one function)
        pParam->result = CLREventWaitHelper2(pParam->handle, pParam->dwMilliseconds, pParam->alertable);
    }
    PAL_EXCEPT (EXCEPTION_EXECUTE_HANDLER)
    {
        param.result = WAIT_FAILED;
    }
    PAL_ENDTRY;

    return param.result;
}


DWORD CLREventBase::Wait(DWORD dwMilliseconds, BOOL alertable, PendingSync *syncState)
{
    WRAPPER_NO_CONTRACT;
    return WaitEx(dwMilliseconds, alertable?WaitMode_Alertable:WaitMode_None,syncState);
}


DWORD CLREventBase::WaitEx(DWORD dwMilliseconds, WaitMode mode, PendingSync *syncState)
{
    BOOL alertable = (mode & WaitMode_Alertable)!=0;
    CONTRACTL
    {
        if (alertable)
        {
            THROWS;               // Thread::DoAppropriateWait can throw
        }
        else
        {
            NOTHROW;
        }
        if (GetThread())
        {
            if (alertable)
                GC_TRIGGERS;
            else
                GC_NOTRIGGER;
        }
        else
        {
            DISABLED(GC_TRIGGERS);
        }
        PRECONDITION(m_handle != INVALID_HANDLE_VALUE); // Handle has to be valid
    }
    CONTRACTL_END;


    _ASSERTE(Thread::Debug_AllowCallout());

    Thread * pThread = GetThread();

#ifdef _DEBUG
    // If a CLREvent is OS event only, we can not wait for the event on a managed thread
    if (IsOSEvent())
        _ASSERTE (pThread == NULL);
#endif
    _ASSERTE((pThread != NULL) || !g_fEEStarted || dbgOnly_IsSpecialEEThread());

    {
        if (pThread && alertable) {
            DWORD dwRet = WAIT_FAILED;
            dwRet = pThread->DoAppropriateWait(1, &m_handle, FALSE, dwMilliseconds,
                                              mode,
                                              syncState);
            return dwRet;
        }
        else {
            _ASSERTE (syncState == NULL);
            return CLREventWaitHelper(m_handle,dwMilliseconds,alertable);
        }
    }
}

void CLRSemaphore::Create (DWORD dwInitial, DWORD dwMax)
{
    CONTRACTL
    {
      THROWS;
      GC_NOTRIGGER;
      PRECONDITION(m_handle == INVALID_HANDLE_VALUE);
    }
    CONTRACTL_END;

    {
        HANDLE h = WszCreateSemaphore(NULL,dwInitial,dwMax,NULL);
        if (h == NULL) {
            ThrowOutOfMemory();
        }
        m_handle = h;
    }
}


void CLRSemaphore::Close()
{
    LIMITED_METHOD_CONTRACT;

    if (m_handle != INVALID_HANDLE_VALUE) {
        CloseHandle(m_handle);
        m_handle = INVALID_HANDLE_VALUE;
    }
}

BOOL CLRSemaphore::Release(LONG lReleaseCount, LONG *lpPreviousCount)
{
    CONTRACTL
    {
      NOTHROW;
      GC_NOTRIGGER;
      PRECONDITION(m_handle != INVALID_HANDLE_VALUE);
    }
    CONTRACTL_END;

    {
        return ::ReleaseSemaphore(m_handle, lReleaseCount, lpPreviousCount);
    }
}


DWORD CLRSemaphore::Wait(DWORD dwMilliseconds, BOOL alertable)
{
    CONTRACTL
    {
        if (GetThread() && alertable)
        {
            THROWS;               // Thread::DoAppropriateWait can throw
        }
        else
        {
            NOTHROW;
        }
        if (GetThread())
        {
            if (alertable)
                GC_TRIGGERS;
            else
                GC_NOTRIGGER;
        }
        else
        {
            DISABLED(GC_TRIGGERS);
        }
        PRECONDITION(m_handle != INVALID_HANDLE_VALUE); // Invalid to have invalid handle
    }
    CONTRACTL_END;


    Thread *pThread = GetThread();
    _ASSERTE (pThread || !g_fEEStarted || dbgOnly_IsSpecialEEThread());

    {
        // TODO wwl: if alertable is FALSE, do we support a host to break a deadlock?
        // Currently we can not call through DoAppropriateWait because of CannotThrowComplusException.
        // We should re-consider this after our code is exception safe.
        if (pThread && alertable) {
            return pThread->DoAppropriateWait(1, &m_handle, FALSE, dwMilliseconds,
                                              alertable?WaitMode_Alertable:WaitMode_None,
                                              NULL);
        }
        else {
            DWORD result = WAIT_FAILED;
            EX_TRY
            {
                result = WaitForSingleObjectEx(m_handle,dwMilliseconds,alertable);
            }
            EX_CATCH
            {
                result = WAIT_FAILED;
            }
            EX_END_CATCH(SwallowAllExceptions);
            return result;
        }
    }
}

void CLRLifoSemaphore::Create(INT32 initialSignalCount, INT32 maximumSignalCount)
{
    CONTRACTL
    {
        THROWS;
        GC_NOTRIGGER;
    }
    CONTRACTL_END;

    _ASSERTE(maximumSignalCount > 0);
    _ASSERTE(maximumSignalCount < (UINT16)0xFFFF);
    _ASSERTE(initialSignalCount <= maximumSignalCount);
    _ASSERTE(m_handle == nullptr);

#ifdef FEATURE_PAL
    HANDLE h = WszCreateSemaphore(nullptr, 0, maximumSignalCount, nullptr);
#else // !FEATURE_PAL
    HANDLE h = CreateIoCompletionPort(INVALID_HANDLE_VALUE, nullptr, 0, maximumSignalCount);
#endif // FEATURE_PAL
    if (h == nullptr)
    {
        ThrowOutOfMemory();
    }

    m_handle = h;
    m_counts.signalCount = initialSignalCount;
    INDEBUG(m_maximumSignalCount = maximumSignalCount);
}

void CLRLifoSemaphore::Close()
{
    LIMITED_METHOD_CONTRACT;

    if (m_handle == nullptr)
    {
        return;
    }

    CloseHandle(m_handle);
    m_handle = nullptr;
}

bool CLRLifoSemaphore::WaitForSignal(DWORD timeoutMs)
{
    CONTRACTL
    {
        NOTHROW;
        GC_NOTRIGGER;
    }
    CONTRACTL_END;

    _ASSERTE(timeoutMs != 0);
    _ASSERTE(m_handle != nullptr);
    _ASSERTE(m_counts.VolatileLoadWithoutBarrier().waiterCount != (UINT16)0);

    while (true)
    {
        // Wait for a signal
        BOOL waitSuccessful = WaitForSignalOnce(timeoutMs);

        if (!waitSuccessful)
        {
            // Unregister the waiter. The wait subsystem used above guarantees that a thread that wakes due to a timeout does
            // not observe a signal to the object being waited upon.
            Counts toSubtract;
            ++toSubtract.waiterCount;
            Counts countsBeforeUpdate = m_counts.ExchangeAdd(-toSubtract);
            _ASSERTE(countsBeforeUpdate.waiterCount != (UINT16)0);
            return false;
        }

        // Unregister the waiter if this thread will not be waiting anymore, and try to acquire the semaphore
        Counts counts = m_counts.VolatileLoadWithoutBarrier();
        while (true)
        {
            _ASSERTE(counts.waiterCount != (UINT16)0);
            Counts newCounts = counts;
            if (counts.signalCount != 0)
            {
                --newCounts.signalCount;
                --newCounts.waiterCount;
            }

            // This waiter has woken up and this needs to be reflected in the count of waiters signaled to wake
            _ASSERTE(newCounts.countOfWaitersSignaledToWake != (UINT16)0);
            --newCounts.countOfWaitersSignaledToWake;

            Counts countsBeforeUpdate = m_counts.CompareExchange(newCounts, counts);
            if (countsBeforeUpdate == counts)
            {
                if (counts.signalCount != 0)
                {
                    return true;
                }
                break;
            }

            counts = countsBeforeUpdate;
        }
    }
}

bool CLRLifoSemaphore::WaitForSignalOnce(DWORD timeoutMs)
{
    CONTRACTL
    {
        NOTHROW;
        GC_NOTRIGGER;
    }
    CONTRACTL_END;

    _ASSERTE(timeoutMs != 0);
    _ASSERTE(m_handle != nullptr);
    _ASSERTE(m_counts.VolatileLoadWithoutBarrier().waiterCount != (UINT16)0);

    // Wait for a signal
    BOOL waitSuccessful;
    {
#ifdef FEATURE_PAL
        // Do a prioritized wait to get LIFO waiter release order
        DWORD waitResult = PAL_WaitForSingleObjectPrioritized(m_handle, timeoutMs);
        _ASSERTE(waitResult == WAIT_OBJECT_0 || waitResult == WAIT_TIMEOUT);
        waitSuccessful = waitResult == WAIT_OBJECT_0;
#else // !FEATURE_PAL
        // I/O completion ports release waiters in LIFO order, see
        // https://msdn.microsoft.com/en-us/library/windows/desktop/aa365198(v=vs.85).aspx
        DWORD numberOfBytes;
        ULONG_PTR completionKey;
        LPOVERLAPPED overlapped;
        waitSuccessful = GetQueuedCompletionStatus(m_handle, &numberOfBytes, &completionKey, &overlapped, timeoutMs);
        _ASSERTE(waitSuccessful || GetLastError() == WAIT_TIMEOUT);
        _ASSERTE(overlapped == nullptr);
#endif // FEATURE_PAL
    }

    return waitSuccessful;
}

bool CLRLifoSemaphore::Wait(DWORD timeoutMs)
{
    WRAPPER_NO_CONTRACT;

    _ASSERTE(m_handle != nullptr);

    // Acquire the semaphore or register as a waiter
    Counts counts = m_counts.VolatileLoadWithoutBarrier();
    while (true)
    {
        _ASSERTE(counts.signalCount <= m_maximumSignalCount);
        Counts newCounts = counts;
        if (counts.signalCount != 0)
        {
            --newCounts.signalCount;
        }
        else if (timeoutMs != 0)
        {
            ++newCounts.waiterCount;
            _ASSERTE(newCounts.waiterCount != (UINT16)0); // overflow check, this many waiters is currently not supported
        }

        Counts countsBeforeUpdate = m_counts.CompareExchange(newCounts, counts);
        if (countsBeforeUpdate == counts)
        {
            return counts.signalCount != 0 || (timeoutMs != 0 && WaitForSignal(timeoutMs));
        }

        counts = countsBeforeUpdate;
    }
}

bool CLRLifoSemaphore::Wait(DWORD timeoutMs, UINT32 spinCount, UINT32 processorCount)
{
    CONTRACTL
    {
        NOTHROW;
        GC_NOTRIGGER;
    }
    CONTRACTL_END;

    _ASSERTE(m_handle != nullptr);

    if (timeoutMs == 0 || spinCount == 0)
    {
        return Wait(timeoutMs);
    }

    #ifdef FEATURE_PAL
        // The PAL's wait subsystem is quite slow, spin more to compensate for the more expensive wait
        spinCount *= 2;
    #endif // FEATURE_PAL

    while (true)
    {
        int registeredAsSpinner = 0;
        // TODO: VS . Spinning is used to hide latencies in Release code path.
        //       the spinCount should be roughly the cost of block/unblock via underlying semaphore.
        //       We use 0x200 now. As any constant, it is wrong on all but some configurations.
        //       The number needs to be dynamicly adjusted since both the semaphore and 
        //       pauses can have costs that vary.
        int spinsRemaining = spinCount;
        // Spin while trying to acquire the semaphore or register as a spinner.
        // After enough spins, try registering as a waiter and exit.
        while (true)
        {
            spinsRemaining--;
            Counts counts = m_counts.VolatileLoadWithoutBarrier();
            Counts newCounts = counts;

            if (newCounts.signalCount != 0)
            {
                newCounts.signalCount--;
                newCounts.spinnerCount -= registeredAsSpinner;
            }
            else if (spinsRemaining <= 0 || (newCounts.spinnerCount > processorCount && registeredAsSpinner))
            {
                newCounts.waiterCount++;
                newCounts.spinnerCount -= registeredAsSpinner;
                _ASSERTE(newCounts.waiterCount != (UINT16)0); // overflow check, this many waiters is not expected
            }
            else if (!registeredAsSpinner)
            {
                newCounts.spinnerCount++;
                _ASSERTE(newCounts.spinnerCount != (UINT16)0); // overflow check, this many spinners is not expected
            }
            else
            {
                _ASSERTE(newCounts.spinnerCount > 0);

                // We have nothing to do.
                // pause and check a few more times before blocking and yielding the core
                System_YieldProcessor();
                continue;
            }

            Counts countsBeforeUpdate = m_counts.CompareExchange(newCounts, counts);
            if (countsBeforeUpdate != counts)
            {
                // there was a contention.
                // only one spinner makes progress at a time and failed attempts have cost, 
                // so pause if there are more spinners.
                if (countsBeforeUpdate.spinnerCount > 1)
                {
                    // use spinsRemaining as a pseudo random number. It is random enough for our purposes here.
                    UINT32 pause = (UINT32)spinsRemaining % (UINT32)countsBeforeUpdate.spinnerCount;
                    for (UINT32 i = 0; i < pause; i++)
                    {
                        System_YieldProcessor();
                    }
                }
                continue;
            }

            if (counts.signalCount != 0)
            {
                // successfully acqured, but was the last spinner.
                // prefetch one if none is waking naturally.
                if (newCounts.spinnerCount == 0)
                {                
                    WakeOne();
                }

                return true;
            }
            if (newCounts.waiterCount != counts.waiterCount)
            {
                break;
            }
            else
            {
                registeredAsSpinner = 1;
            }
        }

        // WAIT FOR A SIGNAL!!!
        BOOL waitSuccessful = WaitForSignalOnce(timeoutMs);

        // Unregister the waiter. The wait subsystem used guarantees that a thread that wakes due to a timeout does
        // not observe a signal to the object being waited upon.
        Counts toSubtract;
        ++toSubtract.waiterCount;

        if (waitSuccessful)
        {
            _ASSERTE(m_counts.countOfWaitersSignaledToWake != (UINT16)0);
            ++toSubtract.countOfWaitersSignaledToWake;
        }

        Counts countsBeforeUpdate = m_counts.ExchangeAdd(-toSubtract);
        _ASSERTE(countsBeforeUpdate.waiterCount != (UINT16)0);

        if (!waitSuccessful)
            return false;

        // we are active again
    }
}

void CLRLifoSemaphore::Release(INT32 releaseCount)
{
    CONTRACTL
    {
        NOTHROW;
        GC_NOTRIGGER;
    }
    CONTRACTL_END;

    _ASSERTE(releaseCount > 0);
    _ASSERTE((UINT32)releaseCount <= m_maximumSignalCount);
    _ASSERTE(m_handle != INVALID_HANDLE_VALUE);

    INT16 countOfWaitersToWake;
    while (true)
    {
        Counts counts = m_counts.VolatileLoadWithoutBarrier();
        Counts newCounts = counts;
        countOfWaitersToWake = 0;

        // Increase the signal count. The addition doesn't overflow because of the limit on the max signal count in Create.
        newCounts.signalCount += releaseCount;
        _ASSERTE(newCounts.signalCount > counts.signalCount);

        // known spinners consume a part of signal count. 
        // the rest will have to wake waiters to be sure all signals are consumed.
        if (newCounts.signalCount > newCounts.spinnerCount)
        {
            countOfWaitersToWake = newCounts.signalCount - newCounts.spinnerCount;
            // but we should never need to wake more waiters than we have.
            // if we are asked for more, it just means some spinners have not registered yet.
            UINT16 canWake = newCounts.waiterCount - newCounts.countOfWaitersSignaledToWake;
            countOfWaitersToWake = min(countOfWaitersToWake, canWake);       
            newCounts.countOfWaitersSignaledToWake += (UINT16)countOfWaitersToWake;
        }

        Counts countsBeforeUpdate = m_counts.CompareExchange(newCounts, counts);
        if (countsBeforeUpdate == counts)
        {
            _ASSERTE(counts.countOfWaitersSignaledToWake <= counts.waiterCount);
            _ASSERTE((UINT32)releaseCount <= m_maximumSignalCount - counts.signalCount);
            if (countOfWaitersToWake == 0)
            {
                return;
            }
            break;
        }
    }

    // Wake waiters
#ifdef FEATURE_PAL
    BOOL released = ReleaseSemaphore(m_handle, countOfWaitersToWake, nullptr);
    _ASSERTE(released);
#else // !FEATURE_PAL
    while (countOfWaitersToWake > 0)
    {
        countOfWaitersToWake--;
        while (!PostQueuedCompletionStatus(m_handle, 0, 0, nullptr))
        {
            // Probably out of memory. It's not valid to stop and throw here, so try again after a delay.
            ClrSleepEx(1, false);
        }
    }
#endif // FEATURE_PAL
}

void CLRLifoSemaphore::WakeOne()
{
    CONTRACTL
    {
        NOTHROW;
        GC_NOTRIGGER;
    }
    CONTRACTL_END;

    Counts counts = m_counts.VolatileLoadWithoutBarrier();
    Counts newCounts = counts;

    if (newCounts.countOfWaitersSignaledToWake + newCounts.spinnerCount == 0 && newCounts.waiterCount != 0)
        return;

    newCounts.countOfWaitersSignaledToWake++;

    Counts countsBeforeUpdate = m_counts.CompareExchange(newCounts, counts);
    if (countsBeforeUpdate != counts)
    {
        return;
    }

    // Wake waiters
#ifdef FEATURE_PAL
    BOOL released = ReleaseSemaphore(m_handle, 1, nullptr);
    _ASSERTE(released);
#else // !FEATURE_PAL
    while (!PostQueuedCompletionStatus(m_handle, 0, 0, nullptr))
    {
        // Probably out of memory. It's not valid to stop and throw here, so try again after a delay.
        ClrSleepEx(1, false);
    }
#endif // FEATURE_PAL
}

void CLRMutex::Create(LPSECURITY_ATTRIBUTES lpMutexAttributes, BOOL bInitialOwner, LPCTSTR lpName)
{
    CONTRACTL
    {
        THROWS;
        GC_NOTRIGGER;
        PRECONDITION(m_handle == INVALID_HANDLE_VALUE && m_handle != NULL);
    }
    CONTRACTL_END;

    m_handle = WszCreateMutex(lpMutexAttributes,bInitialOwner,lpName);
    if (m_handle == NULL)
    {
        ThrowOutOfMemory();
    }
}

void CLRMutex::Close()
{
    LIMITED_METHOD_CONTRACT;

    if (m_handle != INVALID_HANDLE_VALUE)
    {
        CloseHandle(m_handle);
        m_handle = INVALID_HANDLE_VALUE;
    }
}

BOOL CLRMutex::Release()
{
    CONTRACTL
    {
      NOTHROW;
      GC_NOTRIGGER;
      PRECONDITION(m_handle != INVALID_HANDLE_VALUE && m_handle != NULL);
    }
    CONTRACTL_END;

    BOOL fRet = ReleaseMutex(m_handle);
    if (fRet)
    {
        EE_LOCK_RELEASED(this);
    }
    return fRet;
}

DWORD CLRMutex::Wait(DWORD dwMilliseconds, BOOL bAlertable)
{
    CONTRACTL {
        NOTHROW;
        GC_NOTRIGGER;
        CAN_TAKE_LOCK;
        PRECONDITION(m_handle != INVALID_HANDLE_VALUE && m_handle != NULL);
    }
    CONTRACTL_END;

    DWORD fRet = WaitForSingleObjectEx(m_handle, dwMilliseconds, bAlertable);

    if (fRet == WAIT_OBJECT_0)
    {
        EE_LOCK_TAKEN(this);
    }

    return fRet;
}
