// **********************************************************************
//
// Copyright (c) 2003-present ZeroC, Inc. All rights reserved.
//
// **********************************************************************

namespace Ice
{
    namespace retry
    {
        [System.Serializable]
        public class SystemFailure : Ice.SystemException
        {
            public override string
            ice_id()
            {
                return "::SystemFailure";
            }
        }
    }
}
