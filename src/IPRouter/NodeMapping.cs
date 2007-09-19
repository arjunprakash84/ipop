using System;
using System.Net;
using Brunet;
using Brunet.Dht;
using System.Collections;
using System.Threading;

namespace Ipop {
  public class NodeMapping : IRpcHandler {
    public IPAddress ip;
    public string netmask, ipop_namespace;
    public Address address;
    public byte [] mac;
    public BrunetTransport brunet {
      get { return _brunet; }
      set {
        _brunet = value;
        if(value != null) {
          _rpc = RpcManager.GetInstance(_brunet.node);
          _rpc.AddHandler("ipop", this);
        }
      }
    }
    private BrunetTransport _brunet;
    private RpcManager _rpc;
    private string geo_loc = ",";
    private DateTime _last_called = DateTime.UtcNow - TimeSpan.FromHours(48);

    public NodeMapping() {
    }

   public void HandleRpc(ISender caller, string method, IList arguments, object request_state) {
      if(_rpc == null) {
        //In case it's called by local objects without initializing _rpc
        throw new InvalidOperationException("This method has to be called from Rpc");
      }
      object result = new InvalidOperationException("Invalid method");
      if(method.Equals("GetState"))
        result = this.GetState();
      else if(method.Equals("Information"))
        result = this.Information();
      _rpc.SendResult(request_state, result);
    }

    public IDictionary GetState() {
      Hashtable ht = new Hashtable(3);
      ht.Add("ipop_namespace", ipop_namespace == null ? string.Empty : ipop_namespace);
      ht.Add("ip_address", ip == null ? string.Empty : ip.ToString());
      ht.Add("netmask", netmask == null ? string.Empty : netmask);
      return ht;
    }

    public IDictionary Information() {
      DateTime now = DateTime.UtcNow;
      if(now - _last_called > TimeSpan.FromHours(48) || geo_loc.Equals(",")) {
        string local_geo_loc = IPOP_Common.GetMyGeoLoc();
        if(!local_geo_loc.Equals(","))
          geo_loc = local_geo_loc;
      }
      Hashtable ht = new Hashtable(5);
      ht.Add("type", "iprouter");
      ht.Add("geo_loc", geo_loc);
      ht.Add("ip", ip.ToString());
      ht.Add("localips", brunet.node.sys_link.GetLocalIPAddresses(null));
      ht.Add("neighbors", brunet.node.sys_link.GetNeighbors(null));
      return ht;
    }
  }

//  We need a thread to remove a node mapping if our use of it expires -- that is it isn't used for a certain period of time
  public class NodeMappings {
    private ArrayList nodeMappings;

    public NodeMappings() {
      nodeMappings = new ArrayList();
    }

    public void AddNodeMapping(NodeMapping node) {
      nodeMappings.Add(node);
    }

    public NodeMapping GetNode(IPAddress ip) {
      foreach (NodeMapping node in nodeMappings) {
        if(node.ip.Equals(ip))
          return node;
      }
      return null;
    }

    public bool RemoveNodeMapping(IPAddress ip) {
      for (int i = 0; i < nodeMappings.Count; i++) {
        if(((NodeMapping) nodeMappings[i]).ip.Equals(ip)) {
          nodeMappings.Remove(i);
          return true;
        }
      }
      return false;
    }
  }
}
