#Get Base Image from Steam CMD
FROM cm2network/steamcmd
USER root
RUN usermod -aG sudo steam
USER steam

COPY run_steamcmd.sh /home/steam/run_steamcmd.sh

CMD ["/bin/bash", "/home/steam/run_steamcmd.sh"]
