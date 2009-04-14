/*
Copyright (C) 2009  David Wolinsky <davidiw@ufl.edu>, University of Florida

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program; if not, write to the Free Software
Foundation, Inc., 59 Temple Place - Suite 330, Boston, MA  02111-1307, USA.
*/

using Brunet;
using Brunet.Applications;
using Ipop.CondorNode;
using NetworkPackets;
using NetworkPackets.DHCP;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;

using System.Text;
using Ipop.DhtNode;

namespace Ipop.IpopRouter {
  /// <summary> IpopRouter allows Ipop to provide L3 connectivity between
  /// multiple domains with only a single instance per site.</summary>
  /// <remarks> Specifically, a user can have 2 remote clusters set up and
  /// using only a single instance of Ipop per-site connect the two clusters.
  /// Unlike previous versions of Ipop, the advantage are that this does not
  /// require any configuration changes to the individual cluster machines,
  /// still provides dynamic IP addresses for all nodes in the combined
  /// cluster, and allows machines in the same cluster to talk directly with
  /// each other. </remarks>
  public class IpopRouter: CondorIpopNode, IRpcHandler {
    protected Dictionary<MemBlock, MemBlock> _ether_to_ip;
    protected Dictionary<MemBlock, MemBlock> _ip_to_ether;
    protected Dictionary<MemBlock, MemBlock> _pre_dhcp;
    protected Dictionary<MemBlock, DHCPServer> _ether_to_dhcp_server;
    protected DHCPServer _static_dhcp_server;
    /// <summary>A hashtable used to lock operations rather than multiple
    /// locks.</summary>
    protected Hashtable _checked_out;
    protected object _sync;
    /// <summary>Set to true once we have "joined" the network</summary>
    protected bool _connected;
    /// <summary>We use this to set our L3 network</summary>
    protected DHCPConfig _dhcp_config;
    protected DateTime _last_check_node;
    protected int _lock;

    RpcManager _rpc;
    protected Dictionary<MemBlock, Address> _ip_to_addr;
    protected Hashtable _migrate_table;

    public IpopRouter(string NodeConfigPath, string IpopConfigPath) :
      base(NodeConfigPath, IpopConfigPath)
    {
      _ether_to_ip = new Dictionary<MemBlock, MemBlock>();
      _ip_to_ether = new Dictionary<MemBlock, MemBlock>();
      _ether_to_dhcp_server = new Dictionary<MemBlock, DHCPServer>();
      _pre_dhcp = new Dictionary<MemBlock, MemBlock>();
      _checked_out = new Hashtable();
      _dhcp_server = null;
      _connected = false;
      _sync = new object();
      Brunet.StateChangeEvent += NodeStateChange;
      Brunet.HeartBeatEvent += CheckNode;
      _last_check_node = DateTime.UtcNow;
      _lock = 0;

      _rpc = Brunet.Rpc;
      _rpc.AddHandler("IpopMigration", this);
      _ip_to_addr = new Dictionary<MemBlock, Address>();
      _migrate_table = new Hashtable();
    }

    /// <summary>Called from Brunet to notify us if we've come connected.</summary>
    protected void NodeStateChange(Node n, Node.ConnectionState state)
    {
      if(state == Node.ConnectionState.Connected) {
        _connected = true;
      }
    }

    protected void CheckNode(object o, EventArgs ea) {
      lock(_sync) {
        if(_dhcp_config == null) {
          GetDHCPConfig();
          return;
        }
        Brunet.HeartBeatEvent -= CheckNode;
        return;
      }
    }
/*
        // The rest doesn't quite work right yet...
        DateTime now = DateTime.UtcNow;
        if((now - _last_check_node).TotalSeconds < 30) {
          return;
        }
        _last_check_node = now;
      }
      CheckNetwork();
    }
    */


    /// <summary>Parses ARP Packets and writes to the Ethernet the translation.</summary>
    /// <remarks>IpopRouter makes nodes think they are in the same Layer 2 network
    /// so that two nodes in the same network can communicate directly with each
    /// other.  IpopRouter masquerades for those that are not local.</remarks>
    /// <param name="ep">The Ethernet packet to translate</param>
    protected override void HandleARP(MemBlock packet)
    {
      ARPPacket ap = new ARPPacket(packet);

      if(ap.Operation == ARPPacket.Operations.Reply) {
        // This would be a unsolicited ARP
        if(ap.TargetProtoAddress.Equals(IPPacket.BroadcastAddress) &&
            !ap.SenderHWAddress.Equals(EthernetPacket.BroadcastAddress) &&
            !ap.SenderProtoAddress.Equals(IPPacket.BroadcastAddress))
        {
          HandleNewStaticIP(ap.SenderHWAddress, ap.SenderProtoAddress);
        }
        return;
      }

      /* Must return nothing if the node is checking availability of IPs */
      /* Or he is looking himself up. */
      if(_ip_to_ether.ContainsKey(ap.TargetProtoAddress) ||
          ap.SenderProtoAddress.Equals(IPPacket.BroadcastAddress) ||
          ap.SenderProtoAddress.Equals(IPPacket.ZeroAddress) ||
          ap.Operation != ARPPacket.Operations.Request) {
        return;
      }

      if(_dhcp_config == null) {
        return;
      }
    
      _address_resolver.StartResolve(ap.TargetProtoAddress);

      for(int i = 0; i < _local_ip.Length; i++) {
        if((_local_ip[i] & _netmask[i]) != (ap.TargetProtoAddress[i] & _netmask[i])) {
          return;
        }
      }

      ARPPacket response = ap.Respond(EthernetPacket.UnicastAddress);

      EthernetPacket res_ep = new EthernetPacket(ap.SenderHWAddress,
        EthernetPacket.UnicastAddress, EthernetPacket.Types.ARP,
        response.ICPacket);
      Ethernet.Send(res_ep.ICPacket);
    }

    protected override void WriteIP(ICopyable packet)
    {
      MemBlock mp = packet as MemBlock;
      if(mp == null) {
        mp = MemBlock.Copy(packet);
      }

      IPPacket ipp = new IPPacket(mp);
      MemBlock dest = null;
      if(!_ip_to_ether.TryGetValue(ipp.DestinationIP, out dest)) {
        if(_ip_to_addr.ContainsKey(ipp.DestinationIP)) {
          Address addr = _ip_to_addr[ipp.DestinationIP];
          Address send_to = _address_resolver.Resolve(ipp.SourceIP);
          if(send_to == null) {
            return;
          }

          Console.WriteLine("Calling set to " + addr);
          Channel result = new Channel();
          AHSender notifier = new AHExactSender(Brunet, send_to);
          _rpc.Invoke(notifier, result, "IpopMigration.Set", ipp.DestinationIP, addr.ToMemBlock());

//          AHSender forwarder = new AHExactSender(Brunet, addr);
//          forwarder.Send(mp);
        }

        return;
      }

      EthernetPacket res_ep = new EthernetPacket(_ip_to_ether[ipp.DestinationIP],
          EthernetPacket.UnicastAddress, EthernetPacket.Types.IP, mp);
      Ethernet.Send(res_ep.ICPacket);
    }

    /// <summary>Is this our IP?  Are we routing for it?</summary>
    /// <param name="ip">The IP in question.</param>
    protected override bool IsLocalIP(MemBlock ip) {
      return _ip_to_ether.ContainsKey(ip) || ip.Equals(IPPacket.ZeroAddress);
    }

    /// <summary>Let's see if we can route for an IP.  Default is do
    /// nothing!</summary>
    /// <param name="ip">The IP in question.</param>
    protected override void HandleNewStaticIP(MemBlock ether_addr, MemBlock ip) {
      lock(_sync) {
        if(_dhcp_config == null) {
          _pre_dhcp[ether_addr] = ip;
          return;
        }
      }

      Console.WriteLine("HandleNewStaticIP: " + Utils.MemBlockToString(ip, '.'));
      DHCPServer dhcp_server = CheckOutDHCPServer(ether_addr);
      if(dhcp_server == null) {
        return;
      }

      WaitCallback wcb = delegate(object o) {

        byte[] res_ip = null;

        try {
          res_ip = dhcp_server.RequestLease(ip, true,
              Brunet.Address.ToString(),
              _ipop_config.AddressData.Hostname);
        } catch(Exception e) {Console.WriteLine("hmmm: " + e); }

        if(res_ip == null) {
          ProtocolLog.WriteIf(IpopLog.DHCPLog, String.Format(
                "Request for {0} failed!", Utils.MemBlockToString(ip, '.')));
        } else {
          Migrate(ip);
          UpdateMapping(ether_addr, MemBlock.Reference(res_ip));
        }

        CheckInDHCPServer(dhcp_server);
      };

      ThreadPool.QueueUserWorkItem(wcb);
    }

    protected void GetDHCPConfig() {
      if(Interlocked.Exchange(ref _lock, 1) == 1) {
        return;
      }

      WaitCallback wcb = delegate(object o) {
        bool success = false;
        DHCPConfig dhcp_config = null;
        try {
          dhcp_config = DhtNode.DhtDHCPServer.GetDHCPConfig(Dht, _ipop_config.IpopNamespace);
          success = true;
        } catch(Exception e) {
          ProtocolLog.WriteIf(IpopLog.DHCPLog, e.ToString());
        }

        if(success) {
          byte[] ip = Utils.StringToBytes(dhcp_config.IPBase, '.');
          byte[] nm = Utils.StringToBytes(dhcp_config.Netmask, '.');
          UpdateAddressData(MemBlock.Reference(ip), MemBlock.Reference(nm));
          lock(_sync) {
            _dhcp_config = dhcp_config;
            _dhcp_server = new DhtNode.DhtDHCPServer(Dht, _dhcp_config, _ipop_config.EnableMulticast);
          }
        }

        Interlocked.Exchange(ref _lock, 0);

        if(success) {
          foreach(KeyValuePair<MemBlock, MemBlock> kvp in _pre_dhcp) {
            HandleNewStaticIP(kvp.Key, kvp.Value);
          }
        }
      };

      ThreadPool.QueueUserWorkItem(wcb);
    }

    protected DHCPServer CheckOutDHCPServer(MemBlock ether_addr) {
      DHCPServer dhcp_server = null;

      lock(_sync) {
        if(!_ether_to_dhcp_server.TryGetValue(ether_addr, out dhcp_server)) {
          dhcp_server = new DhtNode.DhtDHCPServer(Dht, _dhcp_config, _ipop_config.EnableMulticast);
          _ether_to_dhcp_server.Add(ether_addr, dhcp_server);
        }
      }

      lock(_checked_out.SyncRoot) {
        if(_checked_out.Contains(dhcp_server)) {
          return null;
        }
        _checked_out.Add(dhcp_server, true);
      }

      return dhcp_server;
    }

    protected void CheckInDHCPServer(DHCPServer dhcp_server) {
      lock(_checked_out.SyncRoot) {
        _checked_out.Remove(dhcp_server);
      }
    }

    /// <summary>This is used to process a dhcp packet on the node side, that
    /// includes placing data such as the local Brunet Address, Ipop Namespace,
    /// and other optional parameters in our request to the dhcp server.  When
    /// receiving the results, if it is successful, the results are written to
    /// the TAP device.</summary>
    /// <param name="ipp"> The IPPacket that contains the DHCP Request</param>
    /// <param name="dhcp_params"> an object containing any extra parameters for 
    /// the dhcp server</param>
    /// <returns> true on if dhcp is supported.</returns>
    protected override bool HandleDHCP(IPPacket ipp)
    {
      if(!_connected) {
        return true;
      }

      UDPPacket udpp = new UDPPacket(ipp.Payload);
      DHCPPacket dhcp_packet = new DHCPPacket(udpp.Payload);
      MemBlock ether_addr = dhcp_packet.chaddr;

      if(_dhcp_config == null) {
        return true;
      }

      DHCPServer dhcp_server = CheckOutDHCPServer(ether_addr);
      if(dhcp_server == null) {
        return true;
      }

      MemBlock last_ip = null;
      _ether_to_ip.TryGetValue(ether_addr, out last_ip);
      byte[] last_ipb = (last_ip == null) ? null : (byte[]) last_ip;

      WaitCallback wcb = delegate(object o) {
        try {
          DHCPPacket rpacket = dhcp_server.ProcessPacket(dhcp_packet,
              Brunet.Address.ToString(), last_ipb);

          /* Check our allocation to see if we're getting a new address */
          MemBlock new_addr = rpacket.yiaddr;
          UpdateMapping(ether_addr, new_addr);

          MemBlock destination_ip = ipp.SourceIP;
          if(destination_ip.Equals(IPPacket.ZeroAddress)) {
            destination_ip = IPPacket.BroadcastAddress;
          }

          UDPPacket res_udpp = new UDPPacket(_dhcp_server_port, _dhcp_client_port, rpacket.Packet);
          IPPacket res_ipp = new IPPacket(IPPacket.Protocols.UDP, rpacket.siaddr,
              destination_ip, res_udpp.ICPacket);
          EthernetPacket res_ep = new EthernetPacket(ether_addr, EthernetPacket.UnicastAddress,
              EthernetPacket.Types.IP, res_ipp.ICPacket);
          Ethernet.Send(res_ep.ICPacket);
        }
        catch(Exception e) {
          ProtocolLog.WriteIf(IpopLog.DHCPLog, e.ToString());
        }
        
        CheckInDHCPServer(dhcp_server);
      };

      ThreadPool.QueueUserWorkItem(wcb);
      return true;
    }

    /// <summary>Called when an ethernet address has had its IP address changed
    /// or set for the first time.</summary>
    protected virtual void UpdateMapping(MemBlock ether_addr, MemBlock ip_addr)
    {
      lock(_sync) {
        if(_ether_to_ip.ContainsKey(ether_addr)) {
          if(_ether_to_ip[ether_addr].Equals(ip_addr)) {
            return;
          }
          _ip_to_ether.Remove(_ether_to_ip[ether_addr]);
        }

        _ether_to_ip[ether_addr] = ip_addr;
        _ip_to_ether[ip_addr] = ether_addr;
      }

      ProtocolLog.WriteIf(IpopLog.DHCPLog, String.Format(
        "IP Address for {0} changed to {1}.",
        BitConverter.ToString((byte[]) ether_addr).Replace("-", ":"),
        Utils.MemBlockToString(ip_addr, '.')));
    }

    public static new void Main(String[] args) {
      IpopRouter node = new IpopRouter(args[0], args[1]);
      node.Run();
    }

    public void HandleRpc(ISender caller, string method, IList args, object rs) {
      Console.WriteLine("Handle rpc.... " + method + " " + caller);
      object result = null;
      try {
        if(method.Equals("Set")) {
          MemBlock ip = MemBlock.Reference((byte[]) args[0]);
          Address addr = new AHAddress((byte[]) args[1]);
          ((DhtAddressResolver) _address_resolver).Set(ip, addr);
          result = true;
        } else if(method.Equals("Migrate")) {
          MemBlock ip = MemBlock.Reference((byte[]) args[0]);
          AHSender sender = (AHSender) ((ReqrepManager.ReplyState) caller).ReturnPath;
          Address new_addr = sender.Destination;
          result = PleaseMigrate(ip, new_addr);
        } else {
          throw new Exception("Invalid method");
        }
      }
      catch (Exception e) {
        Console.WriteLine(e);
        result = new AdrException(-32602, e);
      }
      Console.WriteLine("Returning result for " + method);
      _rpc.SendResult(rs, result);
    }

    protected bool PleaseMigrate(MemBlock ip, Address new_addr) {
      Console.WriteLine("Migrate for: {0} from {1}", Utils.MemBlockToString(ip, '.'), new_addr);
      MemBlock ether_addr = null;
      lock(_sync) {
        if(_ip_to_ether.TryGetValue(ip, out ether_addr)) {
          _ip_to_ether.Remove(ip);
          _ether_to_ip.Remove(ether_addr);
          _ether_to_dhcp_server.Remove(ether_addr);
        }
        _ip_to_addr[ip] = new_addr;
      }

      string str_addr = Utils.MemBlockToString(ip, '.');
      MemBlock key = MemBlock.Reference(
          Encoding.UTF8.GetBytes(
            "dhcp:" + _dhcp_config.Namespace + ":" + str_addr
            )
          );

      MemBlock value = MemBlock.Reference(
          Encoding.UTF8.GetBytes(Brunet.Address.ToString())
          );

      Channel chan = new Channel(1);
      chan.CloseEvent += delegate(object o, EventArgs ea) {
        bool success = false;
        try {
          success = (bool) chan.Dequeue();
        } catch {
        }

        Console.WriteLine(_ip_to_ether.ContainsKey(ip));
        Console.WriteLine("Delete: " + success);
      };

      _dht.AsyncDelete(key, value, chan);
      Console.WriteLine("Attempting delete...");
      return true;
    }

    protected void Migrate(MemBlock ip) {
      lock(_sync) {
        if(_migrate_table.Contains(ip)) {
          Console.WriteLine("Already attempting migration.... " + Utils.MemBlockToString(ip, '.'));
          return;
        }
        _migrate_table[ip] = true;
      }

      _ip_to_addr.Remove(ip);

      string str_addr = Utils.MemBlockToString(ip, '.');
      MemBlock key = MemBlock.Reference(
          Encoding.UTF8.GetBytes(
            "dhcp:" + _dhcp_config.Namespace + ":" + str_addr
            )
          );

      Hashtable[] results = _dht.Get(key);
      bool same = false;

      foreach(Hashtable result in results) {
        Address addr = AddressParser.Parse(Encoding.UTF8.GetString((byte[]) result["value"]));
        if(addr.Equals(Brunet.Address)) {
          same = true;
          continue;
        }
        Channel q = new Channel();

        q.CloseEvent += delegate(object o, EventArgs ea) {
          lock(_sync) {
            _migrate_table.Remove(ip);
          }
          Console.WriteLine("Migration complete.... " + addr);
        };

        AHSender sender = new AHExactSender(Brunet, addr);
        _rpc.Invoke(sender, q, "IpopMigration.Migrate", ip);
        Console.WriteLine("Attempting migration... " + addr);
      }
      if(results.Length == 0 || (results.Length == 1 && same)) {
        _migrate_table.Remove(ip);
        Console.WriteLine("Hmm no need to migrate...");
      }
    }
  }
}
