// **********************************************************************
//
// Copyright (c) 2003-2004 ZeroC, Inc. All rights reserved.
//
// This copy of Ice is licensed to you under the terms described in the
// ICE_LICENSE file included in this distribution.
//
// **********************************************************************

import Test.*;

class CallbackClient extends Ice.Application
{
    public int
    run(String[] args)
    {
        Ice.ObjectAdapter adapter;

        {
            adapter = communicator().createObjectAdapter("CallbackReceiverAdapter");
            adapter.activate();
            // Put the print statement after activate(), so that if
            // Ice.PrintAdapterReady is set, the "ready" is the first
            // output from the client, and not the print statement
            // below. Otherwise the Python test scripts will be confused,
            // as they expect the "ready" from the Object Adapter to be
            // the first thing that is printed.
            System.out.print("creating and activating callback receiver adapter... ");
            System.out.flush();
            System.out.println("ok");
        }

        Ice.ObjectPrx routerBase;

        {
            System.out.print("testing stringToProxy for router... ");
            System.out.flush();
            routerBase = communicator().stringToProxy("abc/def:default -p 12347 -t 30000");
            System.out.println("ok");
        }
        
        Glacier2.RouterPrx router;

        {
            System.out.print("testing checked cast for router... ");
            System.out.flush();
            router = Glacier2.RouterPrxHelper.checkedCast(routerBase);
            test(router != null);
            System.out.println("ok");
        }

        {
            System.out.print("installing router with communicator... ");
            System.out.flush();
            communicator().setDefaultRouter(router);
            System.out.println("ok");
        }

        Ice.ObjectPrx base;

        {
            System.out.print("testing stringToProxy for server object... ");
            System.out.flush();
            base = communicator().stringToProxy("callback:tcp -p 12345 -t 10000");
            System.out.println("ok");
        }
            
        {
            System.out.print("trying to ping server before session creation... ");
            System.out.flush();
            try
            {
                base.ice_ping();
                test(false);
            }
            catch(Ice.ObjectNotExistException ex)
            {
                System.out.println("ok");
            }
        }

        Glacier2.SessionPrx session;

        {
            System.out.print("trying to create session with wrong password... ");
            System.out.flush();
            try
            {
                session = router.createSession("dummy", "xxx");
                test(false);
            }
            catch(Glacier2.PermissionDeniedException ex)
            {
                System.out.println("ok");
            }
            catch(Glacier2.CannotCreateSessionException ex)
            {
                test(false);
            }
        }

        {
            System.out.print("trying to destroy non-existing session... ");
            System.out.flush();
            try
            {
                router.destroySession();
                test(false);
            }
            catch(Glacier2.SessionNotExistException ex)
            {
                System.out.println("ok");
            }
        }

        {
            System.out.print("creating session with correct password... ");
            System.out.flush();
            try
            {
                session = router.createSession("dummy", "abc123");
            }
            catch(Glacier2.PermissionDeniedException ex)
            {
                test(false);
            }
            catch(Glacier2.CannotCreateSessionException ex)
            {
                test(false);
            }
            System.out.println("ok");
        }

        {
            System.out.print("trying to create a second session... ");
            System.out.flush();
            try
            {
                router.createSession("dummy", "abc123");
                test(false);
            }
            catch(Glacier2.PermissionDeniedException ex)
            {
                test(false);
            }
            catch(Glacier2.CannotCreateSessionException ex)
            {
                System.out.println("ok");
            }
        }

        {
            System.out.print("pinging server after session creation... ");
            System.out.flush();
            base.ice_ping();
            System.out.println("ok");
        }

        CallbackPrx twoway;

        {
            System.out.print("testing checked cast for server object... ");
            System.out.flush();
            twoway = CallbackPrxHelper.checkedCast(base);
            test(twoway != null);
            System.out.println("ok");
        }

        {
            System.out.print("installing router with object adapter... ");
            System.out.flush();
            adapter.addRouter(router);
            System.out.println("ok");
        }

        String category;

        {
            System.out.print("getting category from router... ");
            System.out.flush();
            category = router.getServerProxy().ice_getIdentity().category;
            System.out.println("ok");
        }

        CallbackReceiverI callbackReceiverImpl;
        Ice.Object callbackReceiver;
        CallbackReceiverPrx twowayR;
        CallbackReceiverPrx fakeTwowayR;
        
        {
            System.out.print("creating and adding callback receiver object... ");
            System.out.flush();
            callbackReceiverImpl = new CallbackReceiverI();
            callbackReceiver = callbackReceiverImpl;
            Ice.Identity callbackReceiverIdent = new Ice.Identity();
            callbackReceiverIdent.name = "callbackReceiver";
            callbackReceiverIdent.category = category;
            twowayR = CallbackReceiverPrxHelper.uncheckedCast(adapter.add(callbackReceiver, callbackReceiverIdent));
            Ice.Identity fakeCallbackReceiverIdent = new Ice.Identity();
            fakeCallbackReceiverIdent.name = "callbackReceiver";
            fakeCallbackReceiverIdent.category = "dummy";
            fakeTwowayR = CallbackReceiverPrxHelper.uncheckedCast(
                adapter.add(callbackReceiver, fakeCallbackReceiverIdent));
            System.out.println("ok");
        }
        
        {
            System.out.print("testing oneway callback... ");
            System.out.flush();
            CallbackPrx oneway = CallbackPrxHelper.uncheckedCast(twoway.ice_oneway());
            CallbackReceiverPrx onewayR = CallbackReceiverPrxHelper.uncheckedCast(twowayR.ice_oneway());
            java.util.Map context = new java.util.HashMap();
            context.put("_fwd", "o");
            oneway.initiateCallback(onewayR, context);
            test(callbackReceiverImpl.callbackOK());
            System.out.println("ok");
        }

        {
            System.out.print("testing twoway callback... ");
            System.out.flush();
            java.util.Map context = new java.util.HashMap();
            context.put("_fwd", "t");
            twoway.initiateCallback(twowayR, context);
            test(callbackReceiverImpl.callbackOK());
            System.out.println("ok");
        }

        {
            System.out.print("ditto, but with user exception... ");
            System.out.flush();
            java.util.Map context = new java.util.HashMap();
            context.put("_fwd", "t");
            try
            {
                twoway.initiateCallbackEx(twowayR, context);
                test(false);
            }
            catch(CallbackException ex)
            {
                test(ex.someValue == 3.14);
                test(ex.someString.equals("3.14"));
            }
            test(callbackReceiverImpl.callbackOK());
            System.out.println("ok");
        }

        {
            System.out.print("trying twoway callback with fake category... ");
            System.out.flush();
            java.util.Map context = new java.util.HashMap();
            context.put("_fwd", "t");
            try
            {
                twoway.initiateCallback(fakeTwowayR, context);
                test(false);
            }
            catch(Ice.ObjectNotExistException ex)
            {
                System.out.println("ok");
            }
        }

        {
            System.out.print("testing server shutdown... ");
            System.out.flush();
            twoway.shutdown();
            // No ping, otherwise the router prints a warning message if it's
            // started with --Ice.Warn.Connections.
            System.out.println("ok");
            /*
              try
              {
                  base.ice_ping();
                  test(false);
              }
              // If we use the glacier router, the exact exception reason gets
              // lost.
              catch(Ice.UnknownLocalException ex)
              {
                  System.out.println("ok");
              }
            */
        }
        
        {
            System.out.print("destroying session... ");
            System.out.flush();
            try
            {
                router.destroySession();
            }
            catch(Glacier2.SessionNotExistException ex)
            {
                test(false);
            }
            System.out.println("ok");
        }
        
        {
            System.out.print("trying to ping server after session destruction... ");
            System.out.flush();
            try
            {
                base.ice_ping();
                test(false);
            }
            catch(Ice.ObjectNotExistException ex)
            {
                System.out.println("ok");
            }
        }

        return 0;
    }

    private static void
    test(boolean b)
    {
        if(!b)
        {
            throw new RuntimeException();
        }
    }
}
