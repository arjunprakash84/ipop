using Brunet;
using Brunet.Applications;
using NetworkPackets;
using NetworkPackets.DNS;
using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;

namespace Ipop
{
    /**
    <summary>A basic abstract DNS server.  Must implement the translation process
    using NameLookUp and AddressLookUp.</summary>
    */
    public abstract class DNS
    {
        /// <summary>The base ip address to perfom lookups on</summary>
        protected MemBlock _base_address;
        /// <summary>The mask for the ip address to perform lookups on.</summary>
        protected MemBlock _netmask;
        /// <summary>Becomes true after the first SetAddressInfo.</summary>
        protected bool _active;
        /// <summary>Domain name is:</summary>
        public static string DomainName = "ipop";
        /// <summary>Default DNS Server assigned as Open DNS server.</summary>
        public static string DefaultDNSServer = "208.67.222.222";
        /// <summary>DNS Server </summary>
        protected IPEndPoint DNSServer;
        /// <summary>Whether DNS server should perform recursive queries</summary>
        protected bool _recursive_available;
        /// <summary>Lock</summary>
        protected object _sync;

        /// <summary>Default constructor</summary>
        /// <param name="ip_address">An IP Address in the range.</param>
        /// <param name="netmask">The netmask for the range.</param>
        public DNS(MemBlock ip_address, MemBlock netmask, string dns_server, bool RA)
        {
            if (dns_server == null)
            {
                dns_server = DefaultDNSServer;
                RA = true;
            }
            
            DNSServer =  new IPEndPoint(IPAddress.Parse(dns_server), 53);
            _recursive_available = RA;
            _sync = new object();
            byte[] ba = new byte[ip_address.Length];
            for (int i = 0; i < ip_address.Length; i++)
            {
                ba[i] = (byte)(ip_address[i] & netmask[i]);
            }
            lock (_sync)
            {
                _base_address = MemBlock.Reference(ba);
                _netmask = netmask;
            }
            _active = true;
        }

        /// <summary>Look up a hostname given a DNS request in the form of IPPacket
        /// </summary>
        /// <param name="req_ipp">An IPPacket containing the DNS request</param>
        /// <returns>An IPPacket containing the results</returns>
        public virtual IPPacket LookUp(IPPacket req_ipp)
        {
            UDPPacket req_udpp = new UDPPacket(req_ipp.Payload);
            DNSPacket dnspacket = new DNSPacket(req_udpp.Payload);
            DNSPacket res_packet = null;
            ICopyable rdnspacket = null;
            try
            {
                string qname_response = String.Empty;
                string qname = dnspacket.Questions[0].QNAME;
                if (dnspacket.Questions[0].QTYPE == DNSPacket.TYPES.A)
                {
                    qname_response = AddressLookUp(qname);
                }
                else if (dnspacket.Questions[0].QTYPE == DNSPacket.TYPES.PTR)
                {
                    if (InRange(qname))
                        qname_response = NameLookUp(qname);
                    else
                        qname_response = null;
                }
                else
                {
                    qname_response = null;
                }

                if (qname_response == null)
                {
                    if (_recursive_available)
                    {
                        res_packet = new DNSPacket(QueryNS(DNSServer,
                                    (byte[])req_udpp.Payload));
                    }
                    else
                    {
                        //code for generating DNS packet for iterative query
                    }
                }
                else
                {
                    Response response = new Response(qname, dnspacket.Questions[0].QTYPE,
                      dnspacket.Questions[0].QCLASS, 1800, qname_response);

                    // For some reason, if RD is set and we don't have RA it Linux `host`
                    // doesn't work!
                    res_packet = new DNSPacket(dnspacket.ID, false,
                      dnspacket.OPCODE, true, dnspacket.RD, dnspacket.RD,
                      dnspacket.Questions, new Response[] { response }, null, null);
                }
                if (res_packet == null)
                    throw new Exception("Unable to resolve name: " + qname);
                rdnspacket = res_packet.ICPacket;
            }
            catch (Exception e)
            {
                ProtocolLog.WriteIf(IpopLog.DNS, e.Message);
                rdnspacket = DNSPacket.BuildFailedReplyPacket(dnspacket);
            }
            UDPPacket res_udpp = new UDPPacket(req_udpp.DestinationPort,
                                               req_udpp.SourcePort, rdnspacket);
            IPPacket res_ipp = new IPPacket(IPPacket.Protocols.UDP,
                                             req_ipp.DestinationIP,
                                             req_ipp.SourceIP,
                                             res_udpp.ICPacket);
            return res_ipp;
        }

        /// <summary>Determines if an IP Address is in  the applicable range for
        /// the DNS server</summary>
        /// <param name="IP">The IP Address to test.</param>
        /// <returns>False if the IP Address or netmask is undefined or the Address
        /// is not in applicable range, True if it is.</returns>
        protected bool InRange(String IP)
        {
            if (_base_address == null || _netmask == null)
            {
                return false;
            }
            byte[] ipb = Utils.StringToBytes(IP, '.');
            for (int i = 0; i < 4; i++)
            {
                if ((ipb[i] & _netmask[i]) != _base_address[i])
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Sends DNS query to DNS Server and returns the response. 
        /// </summary>
        /// <param name="server">The IPEndPoint of the DNS Server 
        /// <param name="requestMessage"> DNS Packet to be sent</param>
        /// <returns></returns>
        protected byte[] QueryNS(IPEndPoint server, byte[] requestMessage)
        {
            int attempts = 0;
            int _udpRetryAttempts = 2;

            while (attempts <= _udpRetryAttempts)
            {
                Socket socket = new Socket(AddressFamily.InterNetwork,
                    SocketType.Dgram, ProtocolType.Udp);
                socket.SetSocketOption(SocketOptionLevel.Socket,
                    SocketOptionName.ReceiveTimeout, 1000);
                socket.SendTo(requestMessage, requestMessage.Length,
                    SocketFlags.None, server);
                byte[] responseMessage = new byte[512];
                try
                {
                    socket.Receive(responseMessage);
                    if ((responseMessage[0] == requestMessage[0]) &&
                        (responseMessage[1] == requestMessage[1]))
                            return responseMessage;
                }
                catch (SocketException)
                {
                    attempts++;
                }
                finally
                {
                    socket.Close();
                }
            }
            return null;
        }

        /// <summary>Called during LookUp to perform translation from hostname to IP</summary>
        /// <param name="name">The name to lookup</param>
        /// <returns>The IP Address or null if none exists for the name</returns>
        public abstract String AddressLookUp(String name);

        /// <summary>Called during LookUp to perfrom a translation from IP to hostname.</summary>
        /// <param name="IP">The IP to look up.</param>
        /// <returns>The name or null if none exists for the IP.</returns>
        public abstract String NameLookUp(String IP);

    }
}