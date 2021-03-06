// **********************************************************************
//
// Copyright (c) 2003-present ZeroC, Inc. All rights reserved.
//
// **********************************************************************

#import <objc/Ice.h>
#import <TestCommon.h>
#import <ExceptionsTest.h>

static int
run(id<ICECommunicator> communicator)
{
    TestExceptionsThrowerPrx* exceptionsAllTests(id<ICECommunicator>);
    TestExceptionsThrowerPrx* thrower = exceptionsAllTests(communicator);
    [thrower shutdown];
    return EXIT_SUCCESS;
}

#if TARGET_OS_IPHONE
#  define main exceptionsClient
#endif

int
main(int argc, char* argv[])
{
#ifdef ICE_STATIC_LIBS
    ICEregisterIceSSL(YES);
    ICEregisterIceWS(YES);
#if TARGET_OS_IPHONE && !TARGET_IPHONE_SIMULATOR
    ICEregisterIceIAP(YES);
#endif
#endif

    int status;
    @autoreleasepool
    {
        id<ICECommunicator> communicator = nil;
        @try
        {
            ICEInitializationData* initData = [ICEInitializationData initializationData];
            initData.properties = defaultClientProperties(&argc, argv);
            [initData.properties setProperty:@"Ice.MessageSizeMax" value:@"10"]; // 10KB max
            [initData.properties setProperty:@"Ice.Warn.Connections" value:@"0"];
#if TARGET_OS_IPHONE
            initData.prefixTable_ = [NSDictionary dictionaryWithObjectsAndKeys:
                                      @"TestExceptions", @"::Test",
                                      @"TestExceptionsMod", @"::Test::Mod",
                                    nil];
#endif
            communicator = [ICEUtil createCommunicator:&argc argv:argv initData:initData];
            status = run(communicator);
        }
        @catch(ICEException* ex)
        {
            tprintf("%@\n", ex);
            status = EXIT_FAILURE;
        }
        @catch(TestFailedException* ex)
        {
            status = EXIT_FAILURE;
        }

        if(communicator)
        {
            [communicator destroy];
        }
    }
    return status;
}
