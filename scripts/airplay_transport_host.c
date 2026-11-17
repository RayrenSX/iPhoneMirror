#include <winsock2.h>
#include <stdio.h>
#include <stdlib.h>
#include "airplay.h"
static volatile LONG fixture_state=0,fixture_command=0,fixture_tail=0;
static DWORD WINAPI complete_queue_item(void *opaque) {
 LONG command=(LONG)(INT_PTR)opaque;
 Sleep(180);
 if(InterlockedCompareExchange(&fixture_command,0,0)!=command)return 0;
 InterlockedExchange(&fixture_state,1);
 Sleep(250);
 if(InterlockedCompareExchange(&fixture_command,0,0)!=command)return 0;
 InterlockedExchange(&fixture_tail,1);InterlockedExchange(&fixture_state,6);return 0;
}
static void play(void *cls,char *url,double volume,double position) {
 int state,command,tail;
 if (sscanf(url,"http://fixture.invalid/state/%d/%d/%d",&state,&command,&tail)==3) {
  InterlockedExchange(&fixture_command,command);InterlockedExchange(&fixture_tail,tail);
  InterlockedExchange(&fixture_state,state);return;
 }
 if(sscanf(url,"http://fixture.invalid/queue/%d",&command)==1) {
  HANDLE worker;
  command+=100;
  InterlockedExchange(&fixture_command,command);InterlockedExchange(&fixture_tail,0);
  InterlockedExchange(&fixture_state,3);
  worker=CreateThread(NULL,0,complete_queue_item,(void*)(INT_PTR)command,0,NULL);
  if(worker)CloseHandle(worker);return;
 }
 printf("URL %s\n",url);fflush(stdout);
}
static void info(void *cls,double *duration,double *position,double *rate) {
 int state=InterlockedCompareExchange(&fixture_state,0,0);
 *duration=6; *position=InterlockedCompareExchange(&fixture_tail,0,0)?6:1;
 *rate=state==1?1:0;
}
static int state(void *cls,unsigned long long *command) {
 *command=(unsigned long long)InterlockedCompareExchange(&fixture_command,0,0);
 return (int)InterlockedCompareExchange(&fixture_state,0,0);
}
static void log_event(void *cls,int level,const char *message) {fprintf(stderr,"%s\n",message);fflush(stderr);}
int main(void) {
 WSADATA data; if(WSAStartup(MAKEWORD(2,2),&data))return 2;
 _putenv_s("IPHONE_MIRROR_AIRPLAY_MODE","media");
 airplay_callbacks_t callbacks={0};callbacks.video_play=play;callbacks.video_get_play_info=info;callbacks.video_get_play_state=state;
 int error=0;airplay_t *server=airplay_init(10,&callbacks,NULL,&error);
 if(!server)return 3;
 airplay_set_log_level(server,AIRPLAY_LOG_INFO);airplay_set_log_callback(server,log_event,NULL);
 unsigned short port=17011;const char address[6]={2,1,2,3,4,5};
 if(airplay_start(server,&port,address,6,NULL)<0)return 4;
 puts("READY");fflush(stdout);Sleep(90000);
 airplay_stop(server);airplay_destroy(server);WSACleanup();return 0;
}
