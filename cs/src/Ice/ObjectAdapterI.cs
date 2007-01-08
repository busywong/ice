// **********************************************************************
//
// Copyright (c) 2003-2006 ZeroC, Inc. All rights reserved.
//
// This copy of Ice is licensed to you under the terms described in the
// ICE_LICENSE file included in this distribution.
//
// **********************************************************************

namespace Ice
{
    using System;
    using System.Collections;
    using System.Diagnostics;

    public sealed class ObjectAdapterI : LocalObjectImpl, ObjectAdapter
    {
	public string getName()
	{
	    //
	    // No mutex lock necessary, _name is immutable.
	    //
	    return _noConfig ? "" : _name;
	}

	public Communicator getCommunicator()
	{
	    lock(this)
	    {
		checkForDeactivation();
		
		return _communicator;
	    }
	}

	public void activate()
	{
	    IceInternal.LocatorInfo locatorInfo = null;
	    bool registerProcess = false;
	    bool printAdapterReady = false;

	    lock(this)
	    {
		checkForDeactivation();
		
		//
		// If the one off initializations of the adapter are already
		// done, we just need to activate the incoming connection
		// factories and we're done.
		//
		if(_activateOneOffDone)
		{
		    foreach(IceInternal.IncomingConnectionFactory icf in _incomingConnectionFactories)
		    {
			icf.activate();
		    }
		    return;
		}

		//
		// One off initializations of the adapter: update the locator
		// registry and print the "adapter ready" message. We set the
		// _waitForActivate flag to prevent deactivation from other
		// threads while these one off initializations are done.
		//
		_waitForActivate = true;
		
		locatorInfo = _locatorInfo;
		if(!_noConfig)
		{
		    Properties properties = instance_.initializationData().properties;
		    //
		    // DEPRECATED PROPERTY: Remove extra code in future release.
		    //
		    registerProcess = properties.getPropertyAsIntWithDefault(
		    	_propertyPrefix + _name + ".RegisterProcess",
			properties.getPropertyAsInt(_name + ".RegisterProcess")) > 0;
		    printAdapterReady = properties.getPropertyAsInt("Ice.PrintAdapterReady") > 0;
		}
	    }
	    
	    try
	    {
		Ice.Identity dummy = new Ice.Identity();
		dummy.name = "dummy";
		updateLocatorRegistry(locatorInfo, createDirectProxy(dummy), registerProcess);
	    }
	    catch(Ice.LocalException ex)
	    {
		//
		// If we couldn't update the locator registry, we let the
		// exception go through and don't activate the adapter to
		// allow to user code to retry activating the adapter
		// later.
		//
		lock(this)
		{
		    _waitForActivate = false;
		    System.Threading.Monitor.PulseAll(this);
		}
		throw ex;
	    }
		
	    if(printAdapterReady)
	    {
		System.Console.Out.WriteLine(_name + " ready");
	    }

	    lock(this)
	    {
		Debug.Assert(!_deactivated); // Not possible if _waitForActivate = true;
	    
		//
		// Signal threads waiting for the activation.
		//
		_waitForActivate = false;
		System.Threading.Monitor.PulseAll(this);

		_activateOneOffDone = true;
	    
		foreach(IceInternal.IncomingConnectionFactory icf in _incomingConnectionFactories)
		{
		    icf.activate();
		}
	    }
	}
	
	public void hold()
	{
	    lock(this)
	    {
		checkForDeactivation();
		
		int sz = _incomingConnectionFactories.Count;
		for(int i = 0; i < sz; ++i)
		{
		    IceInternal.IncomingConnectionFactory factory =
			(IceInternal.IncomingConnectionFactory)_incomingConnectionFactories[i];
		    factory.hold();
		}
	    }
	}
	
	public void waitForHold()
	{
	    lock(this)
	    {
		checkForDeactivation();
		
		int sz = _incomingConnectionFactories.Count;
		for(int i = 0; i < sz; ++i)
		{
		    IceInternal.IncomingConnectionFactory factory =
			(IceInternal.IncomingConnectionFactory)_incomingConnectionFactories[i];
		    factory.waitUntilHolding();
		}
	    }
	}
	
	public void deactivate()
	{
	    IceInternal.OutgoingConnectionFactory outgoingConnectionFactory;
	    ArrayList incomingConnectionFactories;
	    IceInternal.LocatorInfo locatorInfo;

	    lock(this)
	    {
		//
		// Ignore deactivation requests if the object adapter has
		// already been deactivated.
		//
		if(_deactivated)
		{
		    return;
		}

		//
		//
		// Wait for activation to complete. This is necessary to not 
		// get out of order locator updates.
		//
		while(_waitForActivate)
		{
		    System.Threading.Monitor.Wait(this);
		}

		if(_routerInfo != null)
		{
		    //
		    // Remove entry from the router manager.
		    //
		    instance_.routerManager().erase(_routerInfo.getRouter());

		    //
		    // Clear this object adapter with the router.
		    //
		    _routerInfo.setAdapter(null);
		}
		
		incomingConnectionFactories = new ArrayList(_incomingConnectionFactories);
		outgoingConnectionFactory = instance_.outgoingConnectionFactory();
		locatorInfo = _locatorInfo;

		_deactivated = true;
		
		System.Threading.Monitor.PulseAll(this);
	    }

	    try
	    {
		updateLocatorRegistry(locatorInfo, null, false);
	    }
	    catch(Ice.LocalException)
	    {
		//
		// We can't throw exceptions in deactivate so we ignore
		// failures to update the locator registry.
		//
	    }

	    //
	    // Must be called outside the thread synchronization, because
	    // Connection::destroy() might block when sending a CloseConnection
	    // message.
	    //
	    int sz = incomingConnectionFactories.Count;
	    for(int i = 0; i < sz; ++i)
	    {
		IceInternal.IncomingConnectionFactory factory =
		    (IceInternal.IncomingConnectionFactory)incomingConnectionFactories[i];
		factory.destroy();
	    }

	    //
	    // Must be called outside the thread synchronization, because
	    // changing the object adapter might block if there are still
	    // requests being dispatched.
	    //
	    outgoingConnectionFactory.removeAdapter(this);
	}
	
	public void waitForDeactivate()
	{
	    IceInternal.IncomingConnectionFactory[] incomingConnectionFactories = null;
	    lock(this)
	    {
	        if(_destroyed)
		{
		    return;
		}

		//
		// Wait for deactivation of the adapter itself, and
		// for the return of all direct method calls using this
		// adapter.
		//
		while(!_deactivated || _directCount > 0)
		{
		    System.Threading.Monitor.Wait(this);
		}
		
		incomingConnectionFactories = 
		    (IceInternal.IncomingConnectionFactory[])_incomingConnectionFactories.ToArray(
		        typeof(IceInternal.IncomingConnectionFactory));
	    }
	    
	    //
	    // Now we wait for until all incoming connection factories are
	    // finished.
	    //
	    for(int i = 0; i < incomingConnectionFactories.Length; ++i)
	    {
	        incomingConnectionFactories[i].waitUntilFinished();
	    }
	}

	public bool isDeactivated()
	{
	    lock(this)
	    {
	        return _deactivated;
	    }
	}

	public void destroy()
	{
	    lock(this)
	    {
	        //
		// Another thread is in the process of destroying the object
		// adapter. Wait for it to finish.
		//
		while(_destroying)
		{
		    System.Threading.Monitor.Wait(this);
		}

		//
		// Object adpater is already destroyed.
		//
		if(_destroyed)
		{
		    return;
		}

		_destroying = true;
	    }

	    //
	    // Deactivate and wait for completion.
	    //
	    deactivate();
	    waitForDeactivate();

	    //
	    // Now it's also time to clean up our servants and servant
	    // locators.
	    //
	    _servantManager.destroy();
	    
	    //
	    // Destroy the thread pool.
	    //
	    if(_threadPool != null)
	    {
		_threadPool.destroy();
		_threadPool.joinWithAllThreads();
	    }

	    IceInternal.ObjectAdapterFactory objectAdapterFactory;
	    
	    lock(this)
	    {
		//
		// Signal that destroying is complete.
		//
		_destroying = false;
		_destroyed = true;
		System.Threading.Monitor.PulseAll(this);
		
		//
		// We're done, now we can throw away all incoming connection
		// factories.
		//
		// We set _incomingConnectionFactories to null because the finalizer
		// must not invoke methods on objects.
		//
		_incomingConnectionFactories = null;
		
		//
		// Remove object references (some of them cyclic).
		//
		instance_ = null;
		_threadPool = null;
		_communicator = null;
		_incomingConnectionFactories = null;
		_routerEndpoints = null;
		_routerInfo = null;
		_publishedEndpoints = null;
		_locatorInfo = null;

		objectAdapterFactory = _objectAdapterFactory;
		_objectAdapterFactory = null;
	    }

	    if(objectAdapterFactory != null)
	    {
	        objectAdapterFactory.removeObjectAdapter(_name);
	    }
	}

	public ObjectPrx add(Ice.Object obj, Identity ident)
	{
	    return addFacet(obj, ident, "");
	}

	public ObjectPrx addFacet(Ice.Object obj, Identity ident, string facet)
	{
	    lock(this)
	    {
		checkForDeactivation();
		checkIdentity(ident);

		//
		// Create a copy of the Identity argument, in case the caller
		// reuses it.
		//
		Identity id = new Identity();
		id.category = ident.category;
		id.name = ident.name;

		_servantManager.addServant(obj, id, facet);

		return newProxy(id, facet);
	    }
	}
	
	public ObjectPrx addWithUUID(Ice.Object obj)
	{
	    return addFacetWithUUID(obj, "");
	}

	public ObjectPrx addFacetWithUUID(Ice.Object obj, string facet)
	{
	    Identity ident = new Identity();
	    ident.category = "";
	    ident.name = Util.generateUUID();
	    
	    return addFacet(obj, ident, facet);
	}
	
	public Ice.Object remove(Identity ident)
	{
	    return removeFacet(ident, "");
	}

	public Ice.Object removeFacet(Identity ident, string facet)
	{
	    lock(this)
	    {
		checkForDeactivation();
		checkIdentity(ident);
		
		return _servantManager.removeServant(ident, facet);
	    }
	}

	public FacetMap removeAllFacets(Identity ident)
	{
	    lock(this)
	    {
	        checkForDeactivation();
		checkIdentity(ident);

		return _servantManager.removeAllFacets(ident);
	    }
	}

	public Ice.Object find(Identity ident)
	{
	    return findFacet(ident, "");
	}

	public Ice.Object findFacet(Identity ident, string facet)
	{
	    lock(this)
	    {
		checkForDeactivation();
		checkIdentity(ident);

		return _servantManager.findServant(ident, facet);
	    }
	}

	public FacetMap findAllFacets(Identity ident)
	{
	    lock(this)
	    {
		checkForDeactivation();
		checkIdentity(ident);

		return _servantManager.findAllFacets(ident);
	    }
	}

	public Ice.Object findByProxy(ObjectPrx proxy)
	{
	    lock(this)
	    {
		checkForDeactivation();

		IceInternal.Reference @ref = ((ObjectPrxHelperBase)proxy).reference__();
		return findFacet(@ref.getIdentity(), @ref.getFacet());
	    }
	}
	
	public void addServantLocator(ServantLocator locator, string prefix)
	{
	    lock(this)
	    {
		checkForDeactivation();
		
		_servantManager.addServantLocator(locator, prefix);
	    }
	}
	
	public ServantLocator findServantLocator(string prefix)
	{
	    lock(this)
	    {
		checkForDeactivation();
		
		return _servantManager.findServantLocator(prefix);
	    }
	}
	
	public ObjectPrx createProxy(Identity ident)
	{
	    lock(this)
	    {
		checkForDeactivation();
		checkIdentity(ident);
		
		return newProxy(ident, "");
	    }
	}
	
	public ObjectPrx createDirectProxy(Identity ident)
	{
	    lock(this)
	    {
		checkForDeactivation();
		checkIdentity(ident);
		
		return newDirectProxy(ident, "");
	    }
	}
	
	public ObjectPrx createIndirectProxy(Identity ident)
	{
	    lock(this)
	    {
		checkForDeactivation();
		checkIdentity(ident);
		
		return newIndirectProxy(ident, "", _id);
	    }
	}
	
	public ObjectPrx createReverseProxy(Identity ident)
	{
	    lock(this)
	    {
		checkForDeactivation();
		checkIdentity(ident);
		
		//
		// Get all incoming connections for this object adapter.
		//
		ArrayList connections = new ArrayList();
		int sz = _incomingConnectionFactories.Count;
		for(int i = 0; i < sz; ++i)
		{
		    IceInternal.IncomingConnectionFactory factory
			= (IceInternal.IncomingConnectionFactory)_incomingConnectionFactories[i];
		    ConnectionI[] cons = factory.connections();
		    for(int j = 0; j < cons.Length; j++)
		    {
			connections.Add(cons[j]);
		    }
		}

		//
		// Create a reference and return a reverse proxy for this
		// reference.
		//
		ConnectionI[] arr = new ConnectionI[connections.Count];
		if(arr.Length != 0)
		{
		    connections.CopyTo(arr, 0);
		}
                IceInternal.Reference @ref = instance_.referenceFactory().create(
		    ident, instance_.getDefaultContext(), "", IceInternal.Reference.Mode.ModeTwoway, 
		    arr);
		return instance_.proxyFactory().referenceToProxy(@ref);
	    }
	}
	
	public void setLocator(LocatorPrx locator)
	{
	    lock(this)
	    {
		checkForDeactivation();
		
		_locatorInfo = instance_.locatorManager().get(locator);
	    }
	}

	public bool isLocal(ObjectPrx proxy)
	{
	    IceInternal.Reference r = ((ObjectPrxHelperBase)proxy).reference__();
	    IceInternal.EndpointI[] endpoints;
	    
	    try
	    {
		IceInternal.IndirectReference ir = (IceInternal.IndirectReference)r;
		if(ir.getAdapterId().Length != 0)
		{
		    //
		    // Proxy is local if the reference adapter id matches this
		    // adapter name.
		    //
		    return ir.getAdapterId().Equals(_id);
		}
		IceInternal.LocatorInfo info = ir.getLocatorInfo();
		if(info != null)
		{
		    bool isCached;
		    endpoints = info.getEndpoints(ir, ir.getLocatorCacheTimeout(), out isCached);
		}
		else
		{
		    return false;
		}
	    }
	    catch(InvalidCastException)
	    {
		endpoints = r.getEndpoints();
	    }
	    
	    lock(this)
	    {
		checkForDeactivation();

		//
		// Proxies which have at least one endpoint in common with the
		// endpoints used by this object adapter's incoming connection
		// factories are considered local.
		//
		for(int i = 0; i < endpoints.Length; ++i)
		{
		    int sz = _incomingConnectionFactories.Count;
		    for(int j = 0; j < sz; j++)
		    {
			IceInternal.IncomingConnectionFactory factory
			    = (IceInternal.IncomingConnectionFactory)_incomingConnectionFactories[j];
			if(factory.equivalent(endpoints[i]))
			{
			    return true;
			}
		    }
		}
		
		//
		// Proxies which have at least one endpoint in common with the
		// router's server proxy endpoints (if any), are also considered
		// local.
		//
		if(_routerInfo != null && _routerInfo.getRouter().Equals(proxy.ice_getRouter()))
		{
		    for(int i = 0; i < endpoints.Length; ++i)
		    {
		        if(_routerEndpoints.BinarySearch(endpoints[i]) >= 0) // _routerEndpoints is sorted.
		        {
			    return true;
		        }
		    }
		}
		
		return false;
	    }
	}
	
	public void flushBatchRequests()
	{
	    ArrayList f;
	    lock(this)
	    {
		f = new ArrayList(_incomingConnectionFactories);
	    }

	    foreach(IceInternal.IncomingConnectionFactory factory in f)
	    {
		factory.flushBatchRequests();
	    }
	}

	public void incDirectCount()
	{
	    lock(this)
	    {
		checkForDeactivation();
		
		Debug.Assert(_directCount >= 0);
		++_directCount;
	    }
	}
	
	public void decDirectCount()
	{
	    lock(this)
	    {
		// Not check for deactivation here!
		
		Debug.Assert(instance_ != null); // Must not be called after destroy().
		
		Debug.Assert(_directCount > 0);
		if(--_directCount == 0)
		{
		    System.Threading.Monitor.PulseAll(this);
		}
	    }
	}
	
	public IceInternal.ThreadPool getThreadPool()
	{
	    // No mutex lock necessary, _threadPool and instance_ are
	    // immutable after creation until they are removed in
	    // destroy().
	    
	    // Not check for deactivation here!
	    
	    Debug.Assert(instance_ != null); // Must not be called after destroy().
	    
	    if(_threadPool != null)
	    {
		return _threadPool;
	    }
	    else
	    {
		return instance_.serverThreadPool();
	    }
	    
	}

	public IceInternal.ServantManager getServantManager()
	{
	    //
	    // No mutex lock necessary, _servantManager is immutable.
	    //
	    return _servantManager;
	}
	
	//
	// Only for use by IceInternal.ObjectAdapterFactory
	//
	public ObjectAdapterI(IceInternal.Instance instance, Communicator communicator,
			      IceInternal.ObjectAdapterFactory objectAdapterFactory, string name, 
			      string endpointInfo, RouterPrx router, bool noConfig)
	{
	    _deactivated = false;
	    instance_ = instance;
	    _communicator = communicator;
	    _objectAdapterFactory = objectAdapterFactory;
	    _servantManager = new IceInternal.ServantManager(instance, name);
	    _activateOneOffDone = false;
	    _name = name;
	    _incomingConnectionFactories = new ArrayList();
	    _publishedEndpoints = new ArrayList();
	    _routerEndpoints = new ArrayList();
	    _routerInfo = null;
	    _directCount = 0;
	    _waitForActivate = false;
	    _noConfig = noConfig;

	    if(_noConfig)
	    {
	        return;
	    }

	    //
	    // DEPRECATED PROPERTIES: Remove extra code in future release.
	    //

	    //
	    // Make sure named adapter has configuration.
	    //
	    Properties properties = instance_.initializationData().properties;
	    string[] oldProps = filterProperties(_name + ".");
	    if(endpointInfo.Length == 0 && router == null)
	    {
	        string[] props = filterProperties(_propertyPrefix + _name + ".");
		if(props.Length == 0 && oldProps.Length == 0)
		{
		    //
		    // These need to be set to prevent warnings/asserts in the destructor.
		    //
		    _deactivated = true;
		    instance_ = null;
		    _communicator = null;
		    _incomingConnectionFactories = null;

		    InitializationException ex = new InitializationException();
		    ex.reason = "Object adapter \"" + _name + "\" requires configuration.";
		    throw ex;
		}
	    }

	    if(oldProps.Length != 0)
	    {
	        string message = "The following properties have been deprecated, please prepend \"Ice.OA.\":";
		for(int i = 0; i < oldProps.Length; ++i)
		{
		    message += "\n    " + oldProps[i];
		}
	        instance_.initializationData().logger.warning(message);
	    }
	    
	    _id = properties.getPropertyWithDefault(_propertyPrefix + _name + ".AdapterId",
	    	properties.getProperty(_name + ".AdapterId"));
	    _replicaGroupId = properties.getPropertyWithDefault(_propertyPrefix + _name + ".ReplicaGroupId",
	    	properties.getProperty(_name + ".ReplicaGroupId"));

	    try
	    {
	        if(router == null)
		{
		    router = RouterPrxHelper.uncheckedCast(
		        instance_.proxyFactory().propertyToProxy(_propertyPrefix + _name + ".Router"));
		    if(router == null)
		    {
		        router = RouterPrxHelper.uncheckedCast(
		            instance_.proxyFactory().propertyToProxy(_name + ".Router"));
		    }
		}
		if(router != null)
		{
		    _routerInfo = instance_.routerManager().get(router);
		    if(_routerInfo != null)
		    {
                        //
                        // Make sure this router is not already registered with another adapter.
                        //
                        if(_routerInfo.getAdapter() != null)
                        {
			    Ice.AlreadyRegisteredException ex = new Ice.AlreadyRegisteredException();
			    ex.kindOfObject = "object adapter with router";
			    ex.id = instance_.identityToString(router.ice_getIdentity());
			    throw ex;
                        }

		        //
		        // Add the router's server proxy endpoints to this object
		        // adapter.
		        //
		        IceInternal.EndpointI[] endpoints = _routerInfo.getServerEndpoints();
		        for(int i = 0; i < endpoints.Length; ++i)
		        {
			    _routerEndpoints.Add(endpoints[i]);
		        }
		        _routerEndpoints.Sort(); // Must be sorted.

		        //
		        // Remove duplicate endpoints, so we have a list of unique endpoints.
		        //
		        for(int i = 0; i < _routerEndpoints.Count-1;)
		        {
			    System.Object o1 = _routerEndpoints[i];
			    System.Object o2 = _routerEndpoints[i + 1];
			    if(o1.Equals(o2))
			    {
			        _routerEndpoints.RemoveAt(i);
			    }
			    else
			    {
				++i;
			    }
		        }

		        //
		        // Associate this object adapter with the router. This way,
		        // new outgoing connections to the router's client proxy will
		        // use this object adapter for callbacks.
		        //
		        _routerInfo.setAdapter(this);
		    
		        //
		        // Also modify all existing outgoing connections to the
		        // router's client proxy to use this object adapter for
		        // callbacks.
		        //      
		        instance_.outgoingConnectionFactory().setRouterInfo(_routerInfo);
		    }
		}
		else
		{
		    //
		    // Parse the endpoints, but don't store them in the adapter.
		    // The connection factory might change it, for example, to
		    // fill in the real port number.
		    //
		    ArrayList endpoints;
		    if(endpointInfo.Length == 0)
		    {
		        endpoints = parseEndpoints(properties.getPropertyWithDefault(
				_propertyPrefix + _name + ".Endpoints",
				properties.getProperty(_name + ".Endpoints")));
		    }
		    else
		    {
		        endpoints = parseEndpoints(endpointInfo);
		    }
		    for(int i = 0; i < endpoints.Count; ++i)
		    {
		        IceInternal.EndpointI endp = (IceInternal.EndpointI)endpoints[i];
		        _incomingConnectionFactories.Add(
			    new IceInternal.IncomingConnectionFactory(instance, endp, this, _name));
		    }
		    if(endpoints.Count == 0)
		    {
		    	IceInternal.TraceLevels tl = instance_.traceLevels();
			if(tl.network >= 2)
			{
			    instance_.initializationData().logger.trace(tl.networkCat,
				 "created adapter `" + _name + "' without endpoints");
			}
		    }
		
		    //
		    // Parse published endpoints. If set, these are used in proxies
		    // instead of the connection factory endpoints.
		    //
		    string endpts = properties.getPropertyWithDefault(_propertyPrefix + _name + ".PublishedEndpoints",
		    	properties.getProperty(_name + ".PublishedEndpoints"));
		    _publishedEndpoints = parseEndpoints(endpts);
		    if(_publishedEndpoints.Count == 0)
		    {
		        foreach(IceInternal.IncomingConnectionFactory factory in _incomingConnectionFactories)
		        {
		            _publishedEndpoints.Add(factory.endpoint());
		        }
		    }

		    //
		    // Filter out any endpoints that are not meant to be published.
		    //
		    ArrayList tmp = new ArrayList();
		    foreach(IceInternal.EndpointI endpoint in _publishedEndpoints)
		    {
		        if(endpoint.publish())
		        {
		            tmp.Add(endpoint);
		        }
		    }
		    _publishedEndpoints = tmp;
		}

		string locatorProperty = _propertyPrefix + _name + ".Locator";
		if(properties.getProperty(locatorProperty).Length > 0)
		{
		    setLocator(LocatorPrxHelper.uncheckedCast(
		        instance_.proxyFactory().propertyToProxy(locatorProperty)));
		}
		else if(properties.getProperty(_name + ".Locator").Length > 0)
		{
		    setLocator(LocatorPrxHelper.uncheckedCast(
		        instance_.proxyFactory().propertyToProxy(_name + ".Locator")));
		}
		else
		{
		    setLocator(instance_.referenceFactory().getDefaultLocator());
		}
		
		if(!instance_.threadPerConnection())
		{
		    if(properties.getProperty(_propertyPrefix + _name + ".ThreadPool.Size").Length != 0 ||
		       properties.getProperty(_propertyPrefix + _name + ".ThreadPool.SizeMax").Length != 0)
		    {
		        int size = properties.getPropertyAsInt(_propertyPrefix + _name + ".ThreadPool.Size");
		        int sizeMax = properties.getPropertyAsInt(_propertyPrefix + _name + ".ThreadPool.SizeMax");
		        if(size > 0 || sizeMax > 0)
		        {
			    _threadPool = 
			        new IceInternal.ThreadPool(instance_, _propertyPrefix + _name + ".ThreadPool", 0);
		        }
		    }
		    else
		    {
		        int size = properties.getPropertyAsInt(_name + ".ThreadPool.Size");
		        int sizeMax = properties.getPropertyAsInt(_name + ".ThreadPool.SizeMax");
		        if(size > 0 || sizeMax > 0)
		        {
			    _threadPool = 
			        new IceInternal.ThreadPool(instance_, _name + ".ThreadPool", 0);
		        }
		    }
		}
	    }
	    catch(LocalException)
	    {
		destroy();
		throw;
	    }
	}
	
#if DEBUG
        ~ObjectAdapterI()
        {   
            lock(this)
            {
		if(!_deactivated)
		{
		    if(!Environment.HasShutdownStarted)
		    {
			instance_.initializationData().logger.warning("object adapter `" + getName() +
								      "' has not been deactivated");
		    }
		    else
		    {
			Console.Error.WriteLine("object adapter `" + getName() + "' has not been deactivated");
		    }
		}
		else if(instance_ != null)
		{
		    if(!Environment.HasShutdownStarted)
		    {
			instance_.initializationData().logger.warning("object adapter `" + getName() +
			                           "' deactivation had not been waited for");
		    }
		    else
		    {
			Console.Error.WriteLine("object adapter `" + getName() + 
						"' deactivation had not been waited for");
		    }
		}
		else
		{
		    IceUtil.Assert.FinalizerAssert(_threadPool == null);
		    //IceUtil.Assert.FinalizerAssert(_servantManager == null); // Not cleared,it needs to be immutable.
		    IceUtil.Assert.FinalizerAssert(_communicator == null);
		    IceUtil.Assert.FinalizerAssert(_incomingConnectionFactories == null);
		    IceUtil.Assert.FinalizerAssert(_directCount == 0);
		    IceUtil.Assert.FinalizerAssert(!_waitForActivate);
		}
            }   
        }
#endif          

	private ObjectPrx newProxy(Identity ident, string facet)
	{
	    if(_id.Length == 0)
	    {
		return newDirectProxy(ident, facet);
	    }
	    else if(_replicaGroupId.Length == 0)
	    {
		return newIndirectProxy(ident, facet, _id);
	    }
	    else
	    {
		return newIndirectProxy(ident, facet, _replicaGroupId);
	    }
	}
	
	private ObjectPrx newDirectProxy(Identity ident, string facet)
	{
	    IceInternal.EndpointI[] endpoints;

	    // 
	    // Use the published endpoints, otherwise use the endpoints from all
	    // incoming connection factories.
	    //
	    int sz = _publishedEndpoints.Count;
	    endpoints = new IceInternal.EndpointI[sz + _routerEndpoints.Count];
	    for(int i = 0; i < sz; ++i)
	    {
	        endpoints[i] = (IceInternal.EndpointI)_publishedEndpoints[i];
	    }

	    //
	    // Now we also add the endpoints of the router's server proxy, if
	    // any. This way, object references created by this object adapter
	    // will also point to the router's server proxy endpoints.
	    //
	    for(int i = 0; i < _routerEndpoints.Count; ++i)
	    {
		endpoints[sz + i] = (IceInternal.EndpointI)_routerEndpoints[i];
	    }
	    
	    //
	    // Create a reference and return a proxy for this reference.
	    //
	    IceInternal.Reference reference =
		instance_.referenceFactory().create(ident, instance_.getDefaultContext(), facet,
						    IceInternal.Reference.Mode.ModeTwoway, false, 
						    instance_.defaultsAndOverrides().defaultPreferSecure, endpoints,
						    null, 
						    instance_.defaultsAndOverrides().defaultCollocationOptimization);
	    return instance_.proxyFactory().referenceToProxy(reference);
	}
	
	private ObjectPrx newIndirectProxy(Identity ident, string facet, string id)
	{
	    //
	    // Create a reference with the adapter id and return a
	    // proxy for the reference.
	    //
	    IceInternal.Reference reference =
		instance_.referenceFactory().create(ident, instance_.getDefaultContext(), facet,
						    IceInternal.Reference.Mode.ModeTwoway, 
						    false, instance_.defaultsAndOverrides().defaultPreferSecure, id,
						    null, _locatorInfo, 
						    instance_.defaultsAndOverrides().defaultCollocationOptimization,
						    instance_.defaultsAndOverrides().defaultLocatorCacheTimeout);
	    return instance_.proxyFactory().referenceToProxy(reference);
	}

	private void checkForDeactivation()
	{
	    if(_deactivated)
	    {
		ObjectAdapterDeactivatedException ex = new ObjectAdapterDeactivatedException();
		ex.name = getName();
		throw ex;
	    }
	}
	
	private static void checkIdentity(Identity ident)
	{
	    if(ident.name == null || ident.name.Length == 0)
	    {
		IllegalIdentityException e = new IllegalIdentityException();
		e.id.name = ident.name;
		e.id.category = ident.category;
		throw e;
	    }	    
	    if(ident.category == null)
	    {
		ident.category = "";
	    }
	}

	private ArrayList parseEndpoints(string endpts)
	{
	    endpts = endpts.ToLower();

	    int beg;
	    int end = 0;

	    string delim = " \t\n\r";

	    ArrayList endpoints = new ArrayList();
	    while(end < endpts.Length)
	    {
		beg = IceUtil.StringUtil.findFirstNotOf(endpts, delim, end);
		if(beg == -1)
		{
		    break;
		}

		end = endpts.IndexOf((System.Char) ':', beg);
		if(end == -1)
		{
		    end = endpts.Length;
		}

		if(end == beg)
		{
		    ++end;
		    continue;
		}

		string s = endpts.Substring(beg, (end) - (beg));
		IceInternal.EndpointI endp = instance_.endpointFactoryManager().create(s);
		if(endp == null)
		{
		    Ice.EndpointParseException e2 = new Ice.EndpointParseException();
		    e2.str = s;
		    throw e2;
		}
		ArrayList endps = endp.expand(true);
		endpoints.AddRange(endps);

		++end;
	    }

	    return endpoints;
	}

	private void updateLocatorRegistry(IceInternal.LocatorInfo locatorInfo, ObjectPrx proxy, bool registerProcess)
	{
	    if(!registerProcess && _id.Length == 0)
	    {
		return; // Nothing to update.
	    }

	    //
	    // We must get and call on the locator registry outside the
	    // thread synchronization to avoid deadlocks. (we can't make
	    // remote calls within the OA synchronization because the
	    // remote call will indirectly call isLocal() on this OA with
	    // the OA factory locked).
	    //
	    // TODO: This might throw if we can't connect to the
	    // locator. Shall we raise a special exception for the
	    // activate operation instead of a non obvious network
	    // exception?
	    //
	    LocatorRegistryPrx locatorRegistry = locatorInfo != null ? locatorInfo.getLocatorRegistry() : null;
	    string serverId = "";
	    if(registerProcess)
	    {
		Debug.Assert(instance_ != null);
		serverId = instance_.initializationData().properties.getProperty("Ice.ServerId");

		if(locatorRegistry == null)
		{
		    instance_.initializationData().logger.warning(
			"object adapter `" + getName() + "' cannot register the process without a locator registry");
		}
		else if(serverId.Length == 0)
		{
		    instance_.initializationData().logger.warning(
			"object adapter `" + getName() + 
			"' cannot register the process without a value for Ice.ServerId");
		}
	    }

	    if(locatorRegistry == null)
	    {
		return;
	    }

	    if(_id.Length > 0)
	    {
		try
		{
		    if(_replicaGroupId.Length == 0)
		    {
			locatorRegistry.setAdapterDirectProxy(_id, proxy);
		    }
		    else
		    {
			locatorRegistry.setReplicatedAdapterDirectProxy(_id, _replicaGroupId, proxy);
		    }
		}
		catch(AdapterNotFoundException)
		{
		    NotRegisteredException ex1 = new NotRegisteredException();
		    ex1.kindOfObject = "object adapter";
		    ex1.id = _id;
		    throw ex1;
		}
		catch(InvalidReplicaGroupIdException)
		{
		    NotRegisteredException ex1 = new NotRegisteredException();
		    ex1.kindOfObject = "replica group";
		    ex1.id = _replicaGroupId;
		    throw ex1;
		}
		catch(AdapterAlreadyActiveException)
		{
		    ObjectAdapterIdInUseException ex1 = new ObjectAdapterIdInUseException();
		    ex1.id = _id;
		    throw ex1;
		}
	    }
	
	    if(registerProcess && serverId.Length > 0)
	    {
		try
		{
		    Process servant = new ProcessI(_communicator);
		    Ice.ObjectPrx process = createDirectProxy(addWithUUID(servant).ice_getIdentity());
		    locatorRegistry.setServerProcessProxy(serverId, ProcessPrxHelper.uncheckedCast(process));
		}
		catch(ServerNotFoundException)
		{
		    NotRegisteredException ex1 = new NotRegisteredException();
		    ex1.id = serverId;
		    ex1.kindOfObject = "server";
		    throw ex1;
		}
	    }
	}

	static private readonly string[] _suffixes = 
	{
            "AdapterId",
            "Endpoints",
            "Locator",
            "PublishedEndpoints",
            "RegisterProcess",
            "ReplicaGroupId",
            "Router",
            "ThreadPool.Size",
            "ThreadPool.SizeMax",
            "ThreadPool.SizeWarn",
            "ThreadPool.StackSize"
	};
	    
	private string[]
	filterProperties(string prefix)
	{
	    ArrayList propertySet = new ArrayList();
	    PropertyDict props = instance_.initializationData().properties.getPropertiesForPrefix(prefix);
	    for(int i = 0; i < _suffixes.Length; ++i)
	    {
	        if(props.Contains(prefix + _suffixes[i]))
		{
		    propertySet.Add(prefix + _suffixes[i]);
		}
	    }

	    return (string[])propertySet.ToArray(typeof(string));
	}

	private sealed class ProcessI : ProcessDisp_
	{
	    public ProcessI(Communicator communicator)
	    {
		_communicator = communicator;
	    }

	    public override void shutdown(Ice.Current current)
	    {
		_communicator.shutdown();
	    }

	    public override void writeMessage(string message, int fd, Ice.Current current)
	    {
		switch(fd)
		{
		    case 1:
		    {
			System.Console.Out.WriteLine(message);
			break;
		    }
		    case 2:
		    {
			System.Console.Error.WriteLine(message);
			break;
		    }
		}
	    }	


	    private Communicator _communicator;
	}
	
	private bool _deactivated;
	private IceInternal.Instance instance_;
	private Communicator _communicator;
	private IceInternal.ObjectAdapterFactory _objectAdapterFactory;
	private IceInternal.ThreadPool _threadPool;
	private IceInternal.ServantManager _servantManager;
	private bool _activateOneOffDone;
	private readonly string _name;
	private readonly string _id;
	private readonly string _replicaGroupId;
	private ArrayList _incomingConnectionFactories;
	private ArrayList _routerEndpoints;
	private IceInternal.RouterInfo _routerInfo;
	private ArrayList _publishedEndpoints;
	private IceInternal.LocatorInfo _locatorInfo;
	private int _directCount;
	private bool _waitForActivate;
	private bool _destroying;
	private bool _destroyed;
	private bool _noConfig;
	static private string _propertyPrefix = "Ice.OA.";
    }
}
