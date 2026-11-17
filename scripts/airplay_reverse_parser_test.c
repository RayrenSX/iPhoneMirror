#include <stdio.h>
#include <string.h>
#include "http_request.h"
int main(void) {
 const char *messages[]={
  "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n",
  "POST /action HTTP/1.1\r\nContent-Length: 2\r\n\r\n{}",
  "RTSP/1.0 200 OK\r\nCSeq: 3\r\nContent-Length: 0\r\n\r\n",
  "GET /playback-info HTTP/1.1\r\nContent-Length: 0\r\n\r\n",
  "EVENT/1.0 200 OK\r\nContent-Length: 0\r\n\r\n"
 };
 const int response[]={1,0,1,0,1};
 for(int round=0;round<40;round++) for(int n=0;n<5;n++) {
  http_request_t *r=http_request_init_response();
  for(size_t i=0;i<strlen(messages[n]);i++) {
   if(http_request_add_data(r,messages[n]+i,1)!=1 || http_request_has_error(r)) {
    fprintf(stderr,"FAIL message %d: %s\n",n,http_request_get_error_name(r));return 1;
   }
  }
  if(!http_request_is_complete(r) || http_request_is_response(r)!=response[n])return 2;
  http_request_destroy(r);
 }
 const char *invalid="EVENX/1.0 200 OK\r\nContent-Length: 0\r\n\r\n";
 http_request_t *r=http_request_init_response();
 http_request_add_data(r,invalid,(int)strlen(invalid));
 if(!http_request_has_error(r))return 3;
 http_request_destroy(r);
 r=http_request_init();
 http_request_add_data(r,messages[4],(int)strlen(messages[4]));
 if(!http_request_has_error(r))return 4;
 http_request_destroy(r);
 puts("PASS 200 fragmented reverse HTTP/RTSP/EVENT responses and control requests; invalid prefix and request-only response rejected");return 0;
}
